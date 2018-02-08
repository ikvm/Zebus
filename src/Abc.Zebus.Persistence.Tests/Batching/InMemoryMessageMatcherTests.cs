﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Abc.Zebus.Lotus;
using Abc.Zebus.Persistence.Matching;
using Abc.Zebus.Persistence.Storage;
using Abc.Zebus.Persistence.Util;
using Abc.Zebus.Testing;
using Abc.Zebus.Testing.Extensions;
using Moq;
using NUnit.Framework;

namespace Abc.Zebus.Persistence.Tests.Batching
{
    [TestFixture]
    public class InMemoryMessageMatcherTests
    {
        private readonly PeerId _peerId = new PeerId("Abc.DestinationPeer.0");
        private readonly PeerId _otherPeerId = new PeerId("Abc.DestinationPeer.1");
        private InMemoryMessageMatcher _matcher;
        private int _batchSize;
        private TimeSpan? _delay;
        private TestBus _bus;
        private Mock<IPersistenceConfiguration> _configurationMock;
        private Mock<IStorage> _storageMock;
        private Action<IList<MatcherEntry>> _storeBatchFunc;
        
        [SetUp]
        public void Setup()
        {
            _batchSize = 1;
            _delay = null;

            _configurationMock = new Mock<IPersistenceConfiguration>();
            _configurationMock.SetupGet(conf => conf.PersisterBatchSize).Returns(() => _batchSize);
            _configurationMock.SetupGet(conf => conf.PersisterDelay).Returns(() => _delay);

            _storageMock = new Mock<IStorage>();

            _bus = new TestBus();
            _matcher = new InMemoryMessageMatcher(_configurationMock.Object, _storageMock.Object, _bus);

            _storeBatchFunc = x => { };
            _storageMock.Setup(x => x.Write(It.IsAny<IList<MatcherEntry>>())).Callback<IList<MatcherEntry>>(items => _storeBatchFunc(items));
        }

        [TearDown]
        public void Teardown()
        {
            _matcher.Stop();
        }

        [Test]
        public void should_batch()
        {
            using (SystemDateTime.PauseTime())
            {
                var persistedBatches = new List<List<MatcherEntry>>();
                CapturePersistedBatches(persistedBatches);
                _batchSize = 5;
                for (var i = 0; i < 25; ++i)
                    EnqueueMessageToPersist();

                _matcher.Start();
                EnqueueMessageToPersist();
                _matcher.Stop();

                persistedBatches.Count.ShouldEqual(6);
                persistedBatches.Count(batch => batch.Count == 5).ShouldEqual(5);
                persistedBatches.Count(batch => batch.Count == 1).ShouldEqual(1);
            }
        }

        [Test]
        public void should_wait_for_delay_before_persisting_messages()
        {
            _delay = 5.Seconds();

            var persistedEntries = new List<MatcherEntry>();
            _storeBatchFunc = persistedEntries.AddRange;

            using (SystemDateTime.PauseTime())
            {
                EnqueueMessageToPersist();

                _matcher.Start();

                Thread.Sleep(500);
                persistedEntries.Count.ShouldEqual(0);

                SystemDateTime.Set(utcNow: SystemDateTime.UtcNow.Add(_delay.Value));

                Wait.Until(() => persistedEntries.Count == 1, 500.Milliseconds());
            }
        }

        [Test, Repeat(3)]
        public void should_not_persist_message_and_ack_received_during_delay()
        {
            _batchSize = 100;
            _delay = 2.Seconds();

            var signal = new ManualResetEventSlim();
            var persistedEntries = new List<MatcherEntry>();
            var persistCallCount = 0;

            _storeBatchFunc = entries =>
            {
                var callCount = Interlocked.Increment(ref persistCallCount);
                if (callCount == 1)
                {
                    persistedEntries.AddRange(entries);
                    signal.Set();
                }
            };

            using (SystemDateTime.PauseTime())
            {
                _matcher.Start();

                var messageId = MessageId.NextId();
                EnqueueMessageToPersist(messageId);
                EnqueueMessageToPersist();
                _matcher.EnqueueAck(_peerId, messageId);

                SystemDateTime.Set(utcNow: SystemDateTime.UtcNow.Add(_delay.Value));

                var persistCalled = signal.Wait(1.Second());

                persistCalled.ShouldBeTrue();
                persistedEntries.Count.ShouldEqual(1);
                persistCallCount.ShouldEqual(1);
            }
        }

        [Test]
        public void should_not_ack_message_with_other_peer_id()
        {
            _batchSize = 100;
            _delay = 2.Seconds();

            var signal = new ManualResetEventSlim();
            var persistedEntries = new List<MatcherEntry>();

            _storeBatchFunc = entries =>
            {
                persistedEntries.AddRange(entries);
                signal.Set();
            };

            using (SystemDateTime.PauseTime())
            {
                _matcher.Start();

                var messageId = MessageId.NextId();
                _matcher.EnqueueMessage(_peerId, messageId, new MessageTypeId("X"), new byte[0]);
                _matcher.EnqueueMessage(_otherPeerId, messageId, new MessageTypeId("X"), new byte[0]);
                _matcher.EnqueueAck(_otherPeerId, messageId);

                SystemDateTime.Set(utcNow: SystemDateTime.UtcNow.Add(_delay.Value));

                var persistCalled = signal.Wait(1.Second());
                persistCalled.ShouldBeTrue();

                var persistedEntry = persistedEntries.ExpectedSingle();
                persistedEntry.PeerId.ShouldEqual(_peerId);
            }
        }

        [Test]
        public void should_clear_pending_entries_on_purge()
        {
            _batchSize = 1;
            var persistedBatches = new List<List<MatcherEntry>>();
            CapturePersistedBatches(persistedBatches);
            // Persisting one batch to initialize the persister
            EnqueueMessageToPersist();
            _matcher.Start();
            Wait.Until(() => persistedBatches.Count == 1, 2);
            // We block the persister and wait for it to be in the persist method to simulate a long persistence
            var batcherShouldResumePersistence = new ManualResetEvent(false);
            var batcherIsWaitingForSignalInThePersistenceLoop = WaitForSignalThenCapturePersistedBatches(batcherShouldResumePersistence, persistedBatches);
            // Enqueing 4 messages, one will be blocked in "long persistence", 3 will be waiting
            EnqueueMessageToPersist();
            EnqueueMessageToPersist();
            EnqueueMessageToPersist();
            EnqueueMessageToPersist();
            batcherIsWaitingForSignalInThePersistenceLoop.WaitOne();

            // The 3 waiting messages should be purged
            var purgedCount = _matcher.Purge();
            batcherShouldResumePersistence.Set();
            _matcher.Stop();

            persistedBatches.Count.ShouldEqual(2);
            purgedCount.ShouldEqual(3);
        }

        [Test]
        public void should_throw_if_enqueuing_after_stop()
        {
            _matcher.Start();
            _matcher.Stop();

            Assert.Throws<InvalidOperationException>(() => EnqueueMessageToPersist());
        }

        [Test]
        public void should_set_signals_instantaneously_when_queue_is_empty()
        {
            _matcher.Start();

            using (var signal1 = new ManualResetEvent(false))
            using (var signal2 = new ManualResetEvent(false))
            {
                _matcher.EnqueueWaitHandle(signal1);
                signal1.WaitOne(500.Milliseconds()).ShouldBeTrue();

                _matcher.EnqueueWaitHandle(signal2);
                signal2.WaitOne(500.Milliseconds()).ShouldBeTrue();
            }
        }

        [Test]
        public void should_wait_for_storage_before_signaling()
        {
            _matcher.Start();

            var storageCompleted = new TaskCompletionSource<int>();
            _storageMock.Setup(x => x.Write(It.IsAny<IList<MatcherEntry>>())).Returns<IList<MatcherEntry>>(items => storageCompleted.Task);

            _matcher.EnqueueMessage(_peerId, MessageId.NextId(), new MessageTypeId("X"), new byte[0]);

            var waitHandle = new ManualResetEvent(false);
            _matcher.EnqueueWaitHandle(waitHandle);

            waitHandle.WaitOne(100.Milliseconds()).ShouldBeFalse();

            storageCompleted.TrySetResult(0);

            waitHandle.WaitOne(100.Milliseconds()).ShouldBeTrue();
        }

        [Test]
        public void should_set_signal_after_message_persistence()
        {
            var messageId = MessageId.NextId();
            var messageBytes = new byte[] { 0x01, 0x02, 0x03 };
            var persistedEntries = new List<MatcherEntry>();
            _storeBatchFunc = persistedEntries.AddRange;

            using (var waitHandle = new ManualResetEvent(false))
            {
                _matcher.EnqueueMessage(_peerId, messageId, new MessageTypeId("Abc.NotARealMessage"), messageBytes);
                _matcher.EnqueueWaitHandle(waitHandle);

                var isSetBeforeStart = waitHandle.WaitOne(100.Milliseconds());
                isSetBeforeStart.ShouldBeFalse();

                _matcher.Start();

                var isSetAfterStart = waitHandle.WaitOne(500.Milliseconds());
                isSetAfterStart.ShouldBeTrue();

                persistedEntries.ExpectedSingle().MessageBytes.ShouldEqual(messageBytes);

                _matcher.Stop();
            }
        }

        [Test]
        public void should_not_throw_if_there_are_only_signals_in_the_batch()
        {
            using (var waitHandle = new ManualResetEvent(false))
            {
                _matcher.Start();
                Assert.DoesNotThrow(() => _matcher.PersistBatch(new List<MatcherEntry>
                {
                    MatcherEntry.EventWaitHandle(waitHandle),
                    MatcherEntry.EventWaitHandle(waitHandle)
                }));
            }
        }

        private void EnqueueAndWaitForErrorCount(int errorCountToReach)
        {
            EnqueueMessageToPersist();
            Wait.Until(() => _bus.Events.OfType<CustomProcessingFailed>().Count() == errorCountToReach, 2);
        }

        private void EnqueueMessageToPersist(MessageId? messageId = null, byte[] messageBytes = null)
        {
            if (messageBytes == null)
                messageBytes = new byte[] { 0x01, 0x02, 0x03 };

            _matcher.EnqueueMessage(_peerId, messageId ?? MessageId.NextId(), new MessageTypeId("Abc.NotARealMessage"), messageBytes);
        }

        private void CapturePersistedBatches(List<List<MatcherEntry>> batchesContainer)
        {
            _storeBatchFunc = msgs => batchesContainer.Add(msgs.ToList());
        }

        private AutoResetEvent WaitForSignalThenCapturePersistedBatches(ManualResetEvent batcherShouldPersist, List<List<MatcherEntry>> persistedBatches)
        {
            var batcherIsInPersistenceMethod = new AutoResetEvent(false);
            _storeBatchFunc = msgs =>
            {
                batcherIsInPersistenceMethod.Set();
                batcherShouldPersist.WaitOne();
                persistedBatches.Add(msgs.ToList());
            };
            return batcherIsInPersistenceMethod;
        }
    }
}
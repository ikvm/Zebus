﻿using System.Reflection;

[assembly: AssemblyProduct("Zebus")]
[assembly: AssemblyDescription("A lightweight Peer to Peer Service Bus")]
[assembly: AssemblyCompany("ABC Arbitrage Asset Management")]
[assembly: AssemblyCopyright("Copyright © ABC Arbitrage Asset Management 2015")]

#if DEBUG
[assembly: AssemblyConfiguration("Debug")]
#else
[assembly: AssemblyConfiguration("Release")]
#endif
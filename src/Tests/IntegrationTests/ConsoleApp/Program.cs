﻿using DanilovSoft.vRPC;
using DanilovSoft.vRPC.Content;
using DanilovSoft.vRPC.Decorator;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

namespace ConsoleApp
{
    class Program
    {
        static async Task Main()
        {
            var listener = new VRpcListener(IPAddress.Any, 1234);
            listener.Start();
            var client = new VRpcClient("localhost", port: 1234, ssl: false, allowAutoConnect: true);
        }
    }
}

﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DanilovSoft.vRPC;
using System.Diagnostics;

namespace Server.Controllers
{
    [AllowAnonymous]
    public class HomeController : ServerController
    {
        private readonly ILogger _logger;
        private readonly Program _program;
        private readonly IClientHomeController _proxy;

        public HomeController(ILogger<HomeController> logger, Program program, IProxy<IClientHomeController> proxy)
        {
            _logger = logger;
            _program = program;
            _proxy = proxy.Proxy;
        }

        [ProducesProtoBuf]
        public DateTime DummyCall(string s)
        {
            Interlocked.Increment(ref Program.ReqCount);
            return DateTime.Now;
        }

        public DateTime Test(TestDto test)
        {
            return DateTime.Now;
        }

        public async Task NotifyTest()
        {
            await _proxy.NotifyAsync();
        }
    }
}

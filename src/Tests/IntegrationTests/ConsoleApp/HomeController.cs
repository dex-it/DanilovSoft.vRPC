﻿using DanilovSoft.vRPC;
using System;
using System.Collections.Generic;
using System.Text;

namespace ConsoleApp
{
    [AllowAnonymous]
    public class HomeController : ClientController
    {
        public HomeController()
        {
            
        }

        public int Test()
        {
            return 123;
        }
    }
}

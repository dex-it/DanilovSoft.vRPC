﻿using System;
using System.Diagnostics;
using System.Security.Claims;

namespace DanilovSoft.vRPC
{
    public class ClientAuthenticatedEventArgs : EventArgs
    {
        public ServerSideConnection Connection { get; }
        public ClaimsPrincipal User { get; }

        [DebuggerStepThrough]
        internal ClientAuthenticatedEventArgs(ServerSideConnection connection, ClaimsPrincipal user)
        {
            Connection = connection;
            User = user;
        }
    }
}
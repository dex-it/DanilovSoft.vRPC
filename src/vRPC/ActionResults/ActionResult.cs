﻿using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Tasks;

namespace DanilovSoft.vRPC
{
    public abstract class ActionResult : IActionResult
    {
        public StatusCode StatusCode { get; internal set; }

        public ActionResult(StatusCode statusCode)
        {
            StatusCode = statusCode;
        }

        public virtual Task ExecuteResultAsync(ActionContext context)
        {
            ExecuteResult(context);
            return Task.CompletedTask;
        }

        public virtual void ExecuteResult(ActionContext context)
        {
            FinalExecuteResult(context);
        }

        private protected virtual void FinalExecuteResult(ActionContext context)
        {

        }
    }
}

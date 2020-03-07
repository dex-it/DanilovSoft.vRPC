﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace DanilovSoft.vRPC
{
    //[DebuggerDisplay(@"\{{ToString(),nq}\}")] // Пусть отображается ToString()
    public sealed class CloseReason
    {
        /// <summary>
        /// "Соединение не установлено."
        /// </summary>
        internal static readonly CloseReason NoConnectionGracifully = new CloseReason(null, null, null, "Соединение не установлено.", null);

        /// <summary>
        /// Является <see langword="true"/> если разъединение завершилось грациозно.
        /// </summary>
        public bool Gracifully => ConnectionError == null;
        /// <summary>
        /// Является <see langword="null"/> если разъединение завершилось грациозно
        /// и не является <see langword="null"/> когда разъединение завершилось не грациозно.
        /// </summary>
        public Exception ConnectionError { get; }
        /// <summary>
        /// Сообщение от удалённой стороны указывающее причину разъединения (может быть <see langword="null"/>).
        /// Если текст совпадает с переданным в метод Shutdown то разъединение произошло по вашей инициативе.
        /// </summary>
        public string CloseDescription { get; }
        /// <summary>
        /// Может быть <see langword="null"/>. Не зависит от <see cref="Gracifully"/>.
        /// </summary>
        public string AdditionalDescription { get; }
        internal WebSocketCloseStatus? CloseStatus { get; }
        /// <summary>
        /// Если был выполнен запрос на остановку сервиса то это свойство будет не <see langword="null"/>.
        /// </summary>
        public ShutdownRequest ShutdownRequest { get; }

        [DebuggerStepThrough]
        internal static CloseReason FromException(WasShutdownException stopRequiredException)
        {
            return new CloseReason(stopRequiredException, null, null, null, stopRequiredException.StopRequiredState);
        }

        [DebuggerStepThrough]
        internal static CloseReason FromException(Exception ex, ShutdownRequest stopRequired, string additionalDescription = null)
        {
            return new CloseReason(ex, null, null, additionalDescription, stopRequired);
        }

        [DebuggerStepThrough]
        internal static CloseReason FromCloseFrame(WebSocketCloseStatus? closeStatus, string closeDescription, string additionalDescription, ShutdownRequest stopRequired)
        {
            return new CloseReason(null, closeStatus, closeDescription, additionalDescription, stopRequired);
        }

        [DebuggerStepThrough]
        private CloseReason(Exception error, WebSocketCloseStatus? closeStatus, string closeDescription, string additionalDescription, ShutdownRequest stopRequired)
        {
            ConnectionError = error;
            CloseDescription = closeDescription;
            CloseStatus = closeStatus;
            AdditionalDescription = additionalDescription;
            ShutdownRequest = stopRequired;
        }

        public override string ToString()
        {
            if (Gracifully)
            {
                if(string.IsNullOrEmpty(CloseDescription))
                {
                    return "Удалённая сторона выполнила нормальное закрытие без объяснения причины";
                }
                else
                {
                    return $"Удалённая сторона выполнила нормальное закрытие: '{CloseDescription}'";
                }
            }
            else
            {
                return $"Соединение оборвано: {AdditionalDescription ?? ConnectionError.Message}";
            }
        }
    }
}
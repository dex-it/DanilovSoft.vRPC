﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using DanilovSoft.vRPC.Source;

namespace DanilovSoft.vRPC
{
    /// <summary>
    /// Является запросом или ответом на запрос.
    /// Содержит <see cref="Stream"/> в который сериализуется заголовок
    /// и сообщение для отправки удалённой стороне.
    /// Необходимо обязательно выполнить Dispose.
    /// </summary>
    internal sealed partial class SerializedMessageToSend : IDisposable
    {
#if DEBUG
        // Что-бы видеть контент в режиме отладки.
        private string? DebugJson => GetDebugJson();

        internal string? GetDebugJson()
        {
            if ((ContentEncoding == null || ContentEncoding == "json") && MemoryPoolBuffer?.WrittenCount > 0 && HeaderSize > 0)
            {
                byte[] copy = MemoryPoolBuffer.WrittenMemory.ToArray();
                string j = Encoding.UTF8.GetString(copy, 0, copy.Length - HeaderSize);
                var element = JsonDocument.Parse(j).RootElement;
                return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
            }
            else
            {
                return null;
            }
        }
#endif

        [SuppressMessage("Usage", "CA2213:Следует высвобождать высвобождаемые поля", Justification = "Dispose выполняется атомарно")]
        private ArrayBufferWriter<byte>? _memPoolBuffer;
        /// <summary>
        /// Если это сообщение является запросом то содержит сериализованные параметры для удалённого метода или любой 
        /// другой тип если это сообщение является ответом на запрос.
        /// Заголовок располагается в конце этого стрима, так как мы не можем сформировать заголовок 
        /// до сериализации тела сообщения.
        /// </summary>
        public ArrayBufferWriter<byte> MemoryPoolBuffer
        {
            get
            {
                Debug.Assert(_memPoolBuffer != null);
                return _memPoolBuffer;
            }
        }
        /// <summary>
        /// Запрос или ответ на запрос.
        /// Может быть статический объект <see cref="RequestMethodMeta"/> или <see cref="ResponseMessage"/>.
        /// </summary>
        public IMessageMeta MessageToSend { get; }
        /// <summary>
        /// Уникальный идентификатор который будет отправлен удалённой стороне.
        /// Может быть Null когда не требуется ответ на запрос.
        /// </summary>
        public int? Uid { get; set; }
        public StatusCode? StatusCode { get; set; }
        public string? ContentEncoding { get; set; }
        /// <summary>
        /// Размер хэдера располагающийся в конце стрима.
        /// </summary>
        public int HeaderSize { get; set; }
        public Multipart[]? Parts { get; set; }

        /// <summary>
        /// Содержит <see cref="MemoryStream"/> в который сериализуется сообщение и заголовок.
        /// Необходимо обязательно выполнить Dispose.
        /// </summary>
        public SerializedMessageToSend(IMessageMeta messageToSend)
        {
            MessageToSend = messageToSend;

            // Арендуем заранее под максимальный размер хэдера.
            _memPoolBuffer = new ArrayBufferWriter<byte>(1024);
        }

        /// <summary>
        /// Возвращает арендованную память обратно в пул.
        /// </summary>
        public void Dispose()
        {
            Interlocked.Exchange(ref _memPoolBuffer, null)?.Dispose();
            if (MessageToSend.IsNotificationRequest)
            {
                CompleteNotification();
            }
#if DEBUG
            GC.SuppressFinalize(this);
#endif
        }

#if DEBUG
        [SuppressMessage("Performance", "CA1821:Удалите пустые завершающие методы", Justification = "Это ловушка для нарушенной логики")]
        ~SerializedMessageToSend()
        {
            Debug.Assert(false, "Деструктор никогда не должен срабатывать");
        }
#endif
    }
}

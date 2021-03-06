﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DanilovSoft.vRPC;
using System.Resources;
using System.Buffers;

namespace Client
{
    class Program
    {
        private const int Port = 65125;
        private static readonly object _conLock = new object();
        private static bool _appExit;
        private static int Threads;

        static void Main()
        {
            Console.Title = "Клиент";
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;

            string ipStr;
            IPAddress ipAddress;
            do
            {
                Console.Write("IP адрес сервера (127.0.0.1): ");
                ipStr = Console.ReadLine();
                if (ipStr == "")
                    ipStr = "127.0.0.1";

            } while (!IPAddress.TryParse(ipStr, out ipAddress));

            string cpusStr;
            int processorCount = Environment.ProcessorCount;
            do
            {
                Console.Write($"Ядер – {processorCount}. Сколько потоков (1): ");
                cpusStr = Console.ReadLine();
                if (cpusStr == "")
                    cpusStr = $"{1}";

            } while (!int.TryParse(cpusStr, out Threads));

            long reqCount = 0;
            int activeThreads = 0;

            var threads = new List<Task>(Threads);
            for (int i = 0; i < Threads; i++)
            {
                var t = Task.Factory.StartNew(() =>
                {
                    if (_appExit)
                        return;

                    Interlocked.Increment(ref activeThreads);

                    using (var client = new VRpcClient(new Uri($"ws://{ipAddress}:{Port}"), true))
                    {
                        Console.CancelKeyPress += (__, e) => Console_CancelKeyPress(e, client);

                        client.ConfigureService(ioc =>
                        {
                            ioc.AddLogging(loggingBuilder =>
                            {
                                loggingBuilder
                                    .AddConsole();
                            });
                        });

                        var controller = client.GetProxy<IBenchmarkController>();
                        
                        while (true)
                        {
                            ConnectResult conResult;
                            while ((conResult = client.ConnectEx()).State == ConnectionState.SocketError)
                            {
                                Thread.Sleep(new Random().Next(2000, 3000));
                            }

                            if (conResult.State == ConnectionState.ShutdownRequest)
                                break;

                            while (true)
                            {
                                try
                                {
                                    controller.VoidNoArgs();
                                }
                                catch (VRpcWasShutdownException)
                                {
                                    return;
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine(ex);
                                    Thread.Sleep(new Random().Next(2000, 3000));
                                    break;
                                }
                                Interlocked.Increment(ref reqCount);
                            }
                        }
                        // Подождать грациозное закрытие.
                        CloseReason closeReason = client.WaitCompletion();
                    }
                    Interlocked.Decrement(ref activeThreads);
                }, TaskCreationOptions.LongRunning);
                threads.Add(t);
            }

            long prev = 0;
            Console.Clear();
            var sw = Stopwatch.StartNew();
            while (threads.TrueForAll(x => x.Status != TaskStatus.RanToCompletion))
            {
                Thread.Sleep(1000);
                long elapsedMs = sw.ElapsedMilliseconds;
                long rCount = Interlocked.Read(ref reqCount);
                ulong reqPerSecond = unchecked((ulong)(rCount - prev));
                prev = rCount;
                sw.Restart();

                var reqPerSec = (int)Math.Round(reqPerSecond * 1000d / elapsedMs);

                PrintConsole(activeThreads, reqPerSec);
            }
            PrintConsole(0, 0);
        }

        private static void PrintConsole(int activeThreads, int reqPerSec)
        {
            lock (_conLock)
            {
                Console.SetCursorPosition(0, 0);
                Console.WriteLine($"Active Threads: {activeThreads.ToString().PadRight(10, ' ')}");
                Console.WriteLine($"Request per second: {reqPerSec.ToString().PadRight(10, ' ')}");
            }
        }

        private static void Console_CancelKeyPress(ConsoleCancelEventArgs e, VRpcClient client)
        {
            _appExit = true;

            if (!e.Cancel)
            {
                e.Cancel = true;
                lock (_conLock)
                {
                    Console.WriteLine("Stopping...");
                }
            }
            client.Shutdown(TimeSpan.FromSeconds(100), "Был нажат Ctrl+C");
        }
    }
}

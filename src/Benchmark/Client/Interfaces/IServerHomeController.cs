﻿using DanilovSoft.vRPC;
using DanilovSoft.vRPC.Content;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Client
{
    [ControllerContract("Benchmark")]
    public interface IBenchmarkController
    {
        [TcpNoDelay]
        void VoidNoArgs();
        void JsonOnlyInt(int id);
        void MultipartOnlyInt(VRpcContent id);
        [Notification]
        void PlainByteArray(byte[] data);
        void MultipartByteArray(VRpcContent data);
        Task DummyCallAsync(int n);
        void Test();
        int Sum(int x1, int x2);
        Task Test3Async();
        Task<int> Test4Async();
        Task<int> Test2Async();
        Task<int> Test0Async();

        [Notification]
        void NotifyTest();

        DateTime Test(TestDto testDto);
        //[Notification]
        Task TestAsync();

        //StreamCall SendFile(FileDescription fileDescription);
    }
}

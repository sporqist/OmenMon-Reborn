  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/
// OmenMon-Reborn additions © 2026 seakyy

using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmenMon.Library;
using Xunit;

namespace OmenMon.Tests {

    public class FanDataPipeServerTests {

        [Fact]
        public void FormatJson_ReturnsExpectedJsonLine() {
            string json = FanDataPipeServer.FormatJson(4022, 3623);
            Assert.Equal("{\"cpu\":4022,\"gpu\":3623}\n", json);

            string zeroJson = FanDataPipeServer.FormatJson(-1, 0);
            Assert.Equal("{\"cpu\":0,\"gpu\":0}\n", zeroJson);
        }

        [Fact]
        public async Task EcCollisionAvoidance_PipeClientReceivesPublishedTelemetry() {
            string testPipeName = "OmenMon_FanData_Test_" + Guid.NewGuid().ToString("N");
            using (var server = new FanDataPipeServer(testPipeName)) {
                server.Start();
                server.Publish(4500, 4200);

                using (var client = new NamedPipeClientStream(".", testPipeName, PipeDirection.In)) {
                    await client.ConnectAsync(3000, TestContext.Current.CancellationToken);
                    Assert.True(client.IsConnected);

                    byte[] buffer = new byte[256];
                    int bytesRead = await client.ReadAsync(buffer, 0, buffer.Length, TestContext.Current.CancellationToken);
                    string received = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    Assert.Contains("\"cpu\":4500", received);
                    Assert.Contains("\"gpu\":4200", received);
                }
            }
        }

        [Fact]
        public void NonBlockingPublish_WhenNoClientConnected() {
            bool origConfig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = true;
                FanDataPipeServer.Instance.Start();

                // Publish when no client is listening should not throw or block
                var watch = System.Diagnostics.Stopwatch.StartNew();
                FanDataPipeServer.Instance.Publish(3000, 2800);
                watch.Stop();

                Assert.True(watch.ElapsedMilliseconds < 500);
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = origConfig;
            }
        }

        private static async Task<string> ReadLineAsync(NamedPipeClientStream client) {
            var line = new StringBuilder();
            byte[] one = new byte[1];
            using var cts = new CancellationTokenSource(3000);
            while (await client.ReadAsync(one, 0, 1, cts.Token) == 1) {
                if (one[0] == (byte) 10) break;
                line.Append((char) one[0]);
            }
            return line.ToString();
        }

        [Fact]
        public async Task Client_ReceivesEachNewValue() {
            string name = "OmenMon_FanData_Test_" + Guid.NewGuid().ToString("N");
            using var server = new FanDataPipeServer(name);
            server.Start();
            server.Publish(1000, 1100);
            using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, TestContext.Current.CancellationToken);
            Assert.Equal("{\"cpu\":1000,\"gpu\":1100}", await ReadLineAsync(client));
            server.Publish(2000, 2100);
            Assert.Equal("{\"cpu\":2000,\"gpu\":2100}", await ReadLineAsync(client));
        }

        // Publish() runs under the hardware lock: a client that connects and never reads
        // must not be able to stall it (it used to write synchronously into the pipe)
        [Fact]
        public async Task Publish_NeverBlocks_OnAClientThatDoesNotRead_AndTheClientIsDropped() {
            string name = "OmenMon_FanData_Test_" + Guid.NewGuid().ToString("N");
            using var server = new FanDataPipeServer(name) { WriteTimeout = TimeSpan.FromMilliseconds(200) };
            server.Start();
            using var stuck = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
            await stuck.ConnectAsync(3000, TestContext.Current.CancellationToken);

            long slowest = 0;
            var total = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; server.DroppedClients == 0 && total.ElapsedMilliseconds < 10000; i++) {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                server.Publish(i, i);
                slowest = Math.Max(slowest, watch.ElapsedMilliseconds);
                if (i % 64 == 0) await Task.Yield();
            }
            Assert.True(slowest < 100, "slowest Publish: " + slowest + " ms");
            Assert.True(server.DroppedClients > 0);

            // The next client is served
            server.Publish(3000, 3100);
            using var next = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
            await next.ConnectAsync(3000, TestContext.Current.CancellationToken);
            Assert.Equal("{\"cpu\":3000,\"gpu\":3100}", await ReadLineAsync(next));
        }

        [Fact]
        public void Configure_False_StopsTheServer_AndPublishDoesNotRestartIt() {
            bool orig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = true;
                FanDataPipeServer.Instance.Start();
                Assert.True(FanDataPipeServer.Instance.IsRunning);
                FanDataPipeServer.Configure(false);
                Assert.False(FanDataPipeServer.Instance.IsRunning);
                FanDataPipeServer.Instance.Publish(1, 1);
                Assert.False(FanDataPipeServer.Instance.IsRunning);
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = orig;
            }
        }

        [Fact]
        public void ConfigToggle_DisablesPipeServer() {
            bool origConfig = FanDataPipeServer.Enabled;
            try {
                FanDataPipeServer.Enabled = false;
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Instance.Start();

                Assert.False(FanDataPipeServer.Instance.IsRunning);
            } finally {
                FanDataPipeServer.Instance.Stop();
                FanDataPipeServer.Enabled = origConfig;
            }
        }
    }
}

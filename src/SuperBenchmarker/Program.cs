﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using CommandLine.Text;

namespace SuperBenchmarker
{
    class Program
    {

        private struct LogData
        {
            public DateTimeOffset EventDate;
            public int Index;
            public int StatusCode;
            public long Millis;
            public IDictionary<string, object> Parameters;
        }

        private static ConcurrentQueue<LogData> _logDataQueue = new ConcurrentQueue<LogData>();

        private static async Task<bool> ProcessLogQueueAsync(StreamWriter writer, CancellationToken cancellationToken)
        {
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await writer.FlushAsync();
                    writer.Close();
                    return true;
                }

                LogData data;
                if (_logDataQueue.TryDequeue(out data))
                {
                    var s = string.Join("\t", new[]
                   {
                        data.EventDate.ToString(),
                        data.Index.ToString(),
                        data.StatusCode.ToString(),
                        data.Millis.ToString(),
                    }.Concat(data.Parameters.Select(x => x.Key + "=" + x.Value)));

                    try
                    {
                        await writer.WriteLineAsync(s);
                    }
                    catch (Exception e)
                    {
                        Trace.WriteLine(e.ToString());
                    }
                }
                else
                {
                    await Task.Delay(250, cancellationToken);
                }
            }
        }

        static void Main(string[] args)
        {

            // to cover our back for all those fire and forgets
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            Console.ForegroundColor = ConsoleColor.Gray;

            ThreadPool.SetMinThreads(200, 100);
            ThreadPool.SetMaxThreads(1000, 200);
            var statusCodes = new ConcurrentBag<HttpStatusCode>();

            var commandLineOptions = new CommandLineOptions();
            bool isHelp = args.Any(x => x == "-?");

            var success = Parser.Default.ParseArguments(args, commandLineOptions);

            if (!success || isHelp)
            {
                if (!isHelp && args.Length > 0)
                    ConsoleWriteLine(ConsoleColor.Red, "error parsing command line");
                return;
            }

            var then = DateTime.Now;
            ConsoleWriteLine(ConsoleColor.DarkCyan, "Starting at {0}", then);

            try
            {
                var requester = new Requester(commandLineOptions);
                var writer = new StreamWriter(commandLineOptions.LogFile) { AutoFlush = false };
                var stopwatch = Stopwatch.StartNew();
                var timeTakens = new ConcurrentBag<double>();
                if (commandLineOptions.SaveResponses)
                {
                    if (string.IsNullOrEmpty(commandLineOptions.ResponseFolder))
                    {
                        commandLineOptions.ResponseFolder = Path.Combine(Environment.CurrentDirectory, "Responses");
                    }

                    if (!Directory.Exists(commandLineOptions.ResponseFolder))
                        Directory.CreateDirectory(commandLineOptions.ResponseFolder);
                }

                ConsoleWriteLine(ConsoleColor.Yellow, "[Press C to stop the test]");
                int total = 0;
                bool disrupted = false;
                var stop = new ConsoleKeyInfo();
                Console.ForegroundColor = ConsoleColor.Cyan;
                var source = new CancellationTokenSource(TimeSpan.FromDays(7));

                Task.Run(() => ProcessLogQueueAsync(writer, source.Token), source.Token);
                Task.Run(() =>
                {
                    stop = Console.ReadKey(true);
                    disrupted = true;
                }, source.Token);

                var result = Parallel.For(0, commandLineOptions.IsDryRun ? 1 : commandLineOptions.NumberOfRequests,
                             new ParallelOptions()
                             {
                                 MaxDegreeOfParallelism = commandLineOptions.Concurrency
                             },
                                 (i, loopstate) =>
                                 {
                                     if (disrupted)
                                     {
                                         ConsoleWriteLine(ConsoleColor.Red, "...");
                                         ConsoleWriteLine(ConsoleColor.Green, "Exiting.... please wait! (it might throw a few more requests)");
                                         ConsoleWriteLine(ConsoleColor.Red, "");
                                         loopstate.Stop();
                                         source.Cancel();
                                     }

                                     var sw = Stopwatch.StartNew();
                                     IDictionary<string, object> parameters;
                                     var statusCode = requester.Next(i, out parameters);
                                     sw.Stop();
                                     if (commandLineOptions.DelayInMillisecond > 0)
                                     {
                                         Thread.Sleep(commandLineOptions.DelayInMillisecond);
                                     }
                                     statusCodes.Add(statusCode);
                                     timeTakens.Add(sw.ElapsedTicks);
                                     var n = Interlocked.Increment(ref total);

                                     // fire and forget not to affect time taken or TPS
                                     _logDataQueue.Enqueue(new LogData()
                                     {
                                         Millis = sw.ElapsedMilliseconds,
                                         Index = n,
                                         EventDate = DateTimeOffset.Now,
                                         StatusCode = (int)statusCode,
                                         Parameters = parameters
                                     });
                                       
                                     if (!commandLineOptions.Verbose)
                                         Console.Write("\r" + total);
                                 }
                    );
               
                Console.WriteLine();
                stopwatch.Stop();

                ConsoleWriteLine(ConsoleColor.Magenta, "---------------Finished!----------------");
                var now = DateTime.Now;
                ConsoleWriteLine(ConsoleColor.DarkCyan, "Finished at {0} (took {1})", now, now - then);

                // waiting for log to catch up
                Thread.Sleep(1000);

                source.Cancel();
                double[] orderedList = (from x in timeTakens
                                        orderby x
                                        select x).ToArray<double>();

                // ----- adding stats of statuses returned
                var stats = statusCodes.GroupBy(x => x)
                           .Select(y => new { Status = y.Key, Count = y.Count() }).OrderByDescending(z => z.Count);

                foreach (var stat in stats)
                {
                    int statusCode = (int)stat.Status;
                    if (statusCode >= 400 && statusCode < 600)
                    {
                        ConsoleWriteLine(ConsoleColor.Red, string.Format("Status {0}:    {1}", statusCode, stat.Count));
                    }
                    else
                    {
                        ConsoleWriteLine(ConsoleColor.Green, string.Format("Status {0}:    {1}", statusCode, stat.Count));
                    }

                }

                Console.WriteLine();

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write("TPS: " + Math.Round(total * 1000f / stopwatch.ElapsedMilliseconds, 1));
                Console.WriteLine(" (requests/second)");
                Console.WriteLine("Max: " + (timeTakens.Max() * 1000 / Stopwatch.Frequency) + "ms");
                Console.WriteLine("Min: " + (timeTakens.Min() * 1000 / Stopwatch.Frequency) + "ms");
                Console.WriteLine("Avg: " + (timeTakens.Average() * 1000 / Stopwatch.Frequency) + "ms");
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine();
                Console.WriteLine("  50%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(50M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  60%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(60M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  70%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(70M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  80%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(80M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  90%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(90M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  95%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(95M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  98%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(98M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("  99%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(99M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");
                Console.WriteLine("99.9%\tbelow " + Math.Round((double)((orderedList.Percentile<double>(99.9M) * 1000.0) / ((double)Stopwatch.Frequency))) + "ms");

            }
            catch (Exception exception)
            {

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(exception);
            }

            Console.ResetColor();
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            try
            {
                Trace.WriteLine(e.Exception.ToString());
            }
            catch
            {

            }
        }

        internal static void ConsoleWrite(ConsoleColor color, string value, params object[] args)
        {
            var foregroundColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(value, args);
            Console.ForegroundColor = foregroundColor;
        }

        internal static void ConsoleWriteLine(ConsoleColor color, string value, params object[] args)
        {
            var foregroundColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(value, args);
            Console.ForegroundColor = foregroundColor;
        }
    }
}

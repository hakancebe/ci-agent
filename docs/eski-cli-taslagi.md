// using CiAgent.Core;

// var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
// var service = new GitHubService(token!);

// var jobs = await service.GetJobsAsync("hakancebe", "ci-agent-pilot", 30797639694);
// // foreach (var job in jobs)
// // {
// //             var failedStep = LogParser.FindFailedStep(job);
// //             if (failedStep != null)
// //             {
// //                 Console.WriteLine($"[{job.Name}] patlayan step: {failedStep.Name}");
// //             }
// // }

// var annotations = await service.GetAnnotationsAsync("hakancebe", "ci-agent-pilot", jobs[0].Id);
// foreach (var a in annotations)
// {
//     Console.WriteLine($"{a.Path}:{a.StartLine} - {a.Message}");
// }
// var filtered = LogParser.FilterAnnotations(annotations);
// Console.WriteLine($"Ham: {annotations.Count}, filtrelenmiş: {filtered.Count}");
// foreach (var a in filtered)
// {
//     Console.WriteLine($"  {a.Path}:{a.StartLine} - {a.Message}");
// }

// var log = await service.DownloadJobLogAsync("hakancebe", "ci-agent-pilot", jobs[0].Id);
// // Console.WriteLine(log.Length);
// // Console.WriteLine(log.Substring(0, Math.Min(500, log.Length)));

// // var firstLine = log.Split('\n')[1];
// // Console.WriteLine($"Önce: {firstLine}");
// // Console.WriteLine($"Sonra: {LogParser.StripTimestamp(firstLine)}");

// // var stepBlocks = LogParser.ExtractStepBlocks(log);
// // Console.WriteLine($"Bulunan step sayısı: {stepBlocks.Count}");
// // foreach (var block in stepBlocks)
// // {
// //     Console.WriteLine("---");
// //     Console.WriteLine(block.Substring(0, Math.Min(100, block.Length)));
// // }
// // Console.WriteLine(log);

// // var buildTestJob = jobs.First(j => j.Name == "build-test");
// // var buildLog = await service.DownloadJobLogAsync("hakancebe", "ci-agent-pilot", buildTestJob.Id);
// // Console.WriteLine("=== BUILD-TEST LOG ===");
// // Console.WriteLine(buildLog);

// var failedJob = jobs.First(j => j.Name == "build-test");
// var failedStep = LogParser.FindFailedStep(failedJob);
// // Console.WriteLine($"Patlayan step: {failedStep?.Name}");

// var failedLog = await service.DownloadJobLogAsync("hakancebe", "ci-agent-pilot", failedJob.Id);
// var failedBlocks = LogParser.ExtractStepBlocks(failedLog);
// // Console.WriteLine($"Step sayısı: {failedBlocks.Count}");

// // // hangi blok patlayan step'e ait, onu tam olarak yazdır
// // foreach (var block in failedBlocks)
// // {
// //     if (block.Contains("dotnet test"))
// //     {
// //         Console.WriteLine("=== TEST BLOĞU ===");
// //         Console.WriteLine(block);
// //     }
// // }

// // var buildStepBlocks = LogParser.ExtractStepBlocks(buildLog);
// // Console.WriteLine($"build-test step sayısı: {buildStepBlocks.Count}");
// // foreach (var block in buildStepBlocks)
// // {
// //     Console.WriteLine("--- BLOCK START ---");
// //     Console.WriteLine(block.Substring(0, Math.Min(60, block.Length)));
// // }

// // foreach (var j in jobs)
// // {
// //     Console.WriteLine($"{j.Name}: {j.Conclusion}");
// // }

// // Console.WriteLine("=== HAM TEST LOG ===");
// // var testLogStart = failedLog.IndexOf("dotnet test");
// // Console.WriteLine(failedLog.Substring(testLogStart, Math.Min(2000, failedLog.Length - testLogStart)));

// //ExtractTestFailure Testleri burada
// foreach (var block in failedBlocks)
// {
//     if (block.Contains("dotnet test"))
//     {
//         Console.WriteLine("=== TEST BLOĞU ===");
//         Console.WriteLine(block);

//         var (path, line, message) = LogParser.ExtractTestFailure(block);
//         Console.WriteLine($"Dosya: {path}, Satır: {line}");
//         Console.WriteLine($"Mesaj: {message}");
//     }
// }

// var debugFailedStep = LogParser.FindFailedStep(failedJob);
// Console.WriteLine($"DEBUG - failedStep.Name: '{debugFailedStep?.Name}'");

// var debugBlocks = LogParser.ExtractStepBlocks(failedLog);
// Console.WriteLine($"DEBUG - blok sayısı: {debugBlocks.Count}");
// foreach (var b in debugBlocks)
// {
//     var contains = b.Contains(debugFailedStep!.Name, StringComparison.OrdinalIgnoreCase);
//     Console.WriteLine($"DEBUG - blok başı: '{b.Substring(0, Math.Min(40, b.Length))}' → içeriyor mu: {contains}");
// }

// var errorContext = LogParser.BuildErrorContext(failedJob, annotations, failedLog);
// Console.WriteLine("=== ERROR CONTEXT ===");
// Console.WriteLine($"Job: {errorContext?.JobName}");
// Console.WriteLine($"Step: {errorContext?.FailedStepName}");
// Console.WriteLine($"Dosya: {errorContext?.FilePath}, Satır: {errorContext?.LineNumber}");
// Console.WriteLine($"Mesaj: {errorContext?.ErrorMessage}");
// Console.WriteLine($"Annotation sayısı: {errorContext?.FilteredAnnotations.Count}");
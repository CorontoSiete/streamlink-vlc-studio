if (args.Contains("--owned-process-fixture", StringComparer.Ordinal))
    return await OwnedProcessTestHost.RunAsync(args);
return await DependencyFreeTestRunner.RunAsync(TestCatalog.All);

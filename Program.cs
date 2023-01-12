﻿namespace PowerShellWindowHost
{
    using System;
    using System.Configuration;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Text.RegularExpressions;
    using PowerShellWindowHost.Extensions;

    public static class Program
    {
        private const string DoubleQuote = "\"";

        public static int Main()
        {
            ////Debugger.Launch();

            string arguments = GetArguments(out string exePath);
            Configure(Path.GetFileName(exePath));

            SimpleLog.Info($"Full Commandline: {Environment.CommandLine}");
            SimpleLog.Info($"Detected Attributes: {arguments}");

            SimpleLog.Info($"RunHiddenConsole Executing Assembly FullName: {exePath}");

            var targetExecutablePath = exePath;

            var startInfo = new ProcessStartInfo
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                Arguments = arguments,
                FileName = targetExecutablePath,
            };

            try
            {
                var proc = Process.Start(startInfo);
                if (proc == null)
                {
                    SimpleLog.Error("Unable to start the target process.");
                    return -7002;
                }

                // process will close as soon as its waiting for interactive input.
                proc.StandardInput.Close();

                proc.WaitForExit();

                SimpleLog.Log(proc.StandardOutput.ReadToEnd());
                return proc.ExitCode;
            }
            catch (Exception ex)
            {
                SimpleLog.Error("Starting target process threw an unknown Exception: " + ex);
                SimpleLog.Log(ex);
                return -7003;
            }
        }

        private static void Configure(string executingAssemblyFileName)
        {
            var logLevelString = ConfigurationManager.AppSettings["LogLevel"];

            var logLocation = ConfigurationManager.AppSettings["LogLocation"];
            if (logLocation != null)
            {
                SimpleLog.SetLogDir(logLocation, true);
            }

            if (logLevelString != null 
                && Enum.TryParse(logLevelString, true, out SimpleLog.Severity logLevel))
            {
                SimpleLog.LogLevel = logLevel;
            }
            else 
            {
                SimpleLog.LogLevel = SimpleLog.Severity.Error;
            }

            SimpleLog.BackgroundTaskDisabled = true;
            SimpleLog.Prefix = $"{executingAssemblyFileName}.";
        }

        private static string GetTargetExecutablePath(string executingAssemblyLocation, string executingAssemblyFileName)
        {
            var match = Regex.Match(executingAssemblyFileName, @"(.+)w(\.\w{1,3})");
            if (!match.Success)
            {
                return null;
            }

            var targetExecutableName = match.Groups[1].Value + match.Groups[2].Value;

            var envPaths = Environment.GetEnvironmentVariable("PATH")
                .SplitExt(Path.PathSeparator);

            var configPaths = ConfigurationManager.AppSettings["TargetExecutablePaths"]
                .SplitExt(Path.PathSeparator);

            // TODO: this can be somewhat expensive in terms of file io. probably a good idea to cache the result for a few seconds.
            var targetExecutablePath = configPaths
                .AppendExt(executingAssemblyLocation)
                .Concat(envPaths)
                .NotNullOrWhitespaceExt()
                .TrimExt()
                .Select(p => Path.Combine(p, targetExecutableName))
                .FirstOrDefault(File.Exists);

            return targetExecutablePath;
        }

        private static string GetArguments(out string execPath)
        {
            var commandLineExecutable = Environment
                .GetCommandLineArgs()[0]
                .Trim();

            var commandLine = Environment
                .CommandLine
                .Trim();

            var argsStartIndex = commandLineExecutable.Length
                + (commandLine.StartsWith(DoubleQuote)
                    ? 2
                    : 0);

            var args = commandLine
                .Substring(argsStartIndex)
                .Trim();

            int argStartIndex2;
            if (args.StartsWith(DoubleQuote))
            {
                argStartIndex2 = args.IndexOf(DoubleQuote, 1) + 1;
                execPath = args.Substring(1, argStartIndex2 - 2);
            } 
            else
            {
                argStartIndex2 = args.IndexOf(' ') + 1;
                execPath = args.Substring(0, argStartIndex2 - 1);
            }

            return args.Substring(argStartIndex2).Trim();
        }
    }
}

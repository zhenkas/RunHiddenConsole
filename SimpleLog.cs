﻿namespace PowerShellWindowHost
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Data.SqlClient;
    using System.Diagnostics;
    using System.IO;
    using System.Reflection;
    using System.Runtime.InteropServices;
    using System.Security.Principal;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using System.Xml.Linq;

    /// <summary>
    /// Simple logging class
    /// </summary>
    /// <license>
    /// The MIT License (MIT)
    /// 
    /// Copyright (c) 2013 - 2014 Jochen Scharr
    /// 
    /// Permission is hereby granted, free of charge, to any person obtaining a copy
    /// of this software and associated documentation files (the "Software"), to deal
    /// in the Software without restriction, including without limitation the rights
    /// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    /// copies of the Software, and to permit persons to whom the Software is
    /// furnished to do so, subject to the following conditions:
    /// 
    /// The above copyright notice and this permission notice shall be included in
    /// all copies or substantial portions of the Software.
    /// 
    /// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    /// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    /// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    /// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    /// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    /// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
    /// THE SOFTWARE.    
    /// </license>
    /// <remarks>
    /// Just write to a file using static methods. By default, the file is named e.g. "2013_04_21.log" and is written to the current working directory. 
    /// This class does not depend on anything. It can be used 'out of the box', no configuration necessary.
    /// 
    /// To keep it simple, it contains just the most essential features like 
    /// 
    /// - Possibility to customize log directory and file name, <see cref="SetLogFile"/>
    /// - Possibility to pass log entry severity (Info, Warning, Error, Exception), <see cref="Log(string, Severity, bool, int)"/>
    /// - Convenience methods <see cref="Info"/>, <see cref="Warning"/>, <see cref="Error"/> to log with respective severity
    /// - Log exceptions, recursively with all inner exceptions, data, stack trace and specific properties of some exception types, <see cref="Log(Exception, bool, int)"/>
    /// - Basic, very simple log level filtering, <see cref="LogLevel"/>.
    /// - Automatically log source, i.e. class and method name where log method was called from (IMHO a very useful feature), <see cref="GetCaller"/>
    /// - Log is formatted as an XML fragment, one XML tag per entry. There's a method <see cref="GetLogFileAsXml(System.DateTime)"/> to complete the XML and get the file as an XML document.
    /// - There is a method <see cref="ShowLogFile(DateTime)"/> to display a log file in the browser. Method <see cref="ShowLogFile()"/> shows the current log file.
    /// - To improve speed, instead of writing each entry to the file immediately, by default, it is passed to a queue and being dequeued and written to disk in a background task. 
    ///   Unfortunately, this makes the class a bit more complicated.
    /// - Thread safety. This class can be used from different threads that all write to the same file. The task ID is logged in each item. 
    ///   This way, one can separate afterwards, which thread logged what.
    /// - New: Possibility to log directly to disk, without using the background task (thread). See argument "useBackgroundTask" in e.g. <see cref="Info"/> or <see cref="Warning"/>:
    /// - New: Possibility to start logging (i.e. the background task) explicitly as opposed to be started automatically on first log. See <see cref="StartExplicitly"/> for details.
    /// </remarks>
    /// <example>
    /// <code>
    /// static void Main(string[] args)
    /// {
    ///     // Log to a sub-directory 'Log' of the current working directory. Prefix log file with 'MyLog_'.
    ///     // This is an optional call and has only to be done once, pereferably before the first log entry is written. 
    ///     SimpleLog.SetLogFile(".\\Log", "MyLog_");
    ///             
    ///     // Write info message to log
    ///     SimpleLog.Info("Test logging started.");
    /// 
    ///     // Write warning message to log
    ///     SimpleLog.Warning("This is a warning.");
    /// 
    ///     // Write error message to log
    ///     SimpleLog.Error("This is an error.");
    /// 
    ///     try
    ///     {
    ///         // For demonstration, do logging in sub-method, throw an exception, 
    ///         // catch it, wrap it in another exception and throw it.
    ///         DoSomething();   
    ///     }
    ///     catch(Exception ex)
    ///     {
    ///         // Write exception with all inner exceptions to log
    ///         SimpleLog.Log(ex);
    ///     }
    /// 
    ///     // Show log file in browser
    ///     SimpleLog.ShowLogFile();
    /// }
    /// 
    /// private static void DoSomething()
    /// {
    ///     SimpleLog.Info("Entering method. See Source which method is meant.");
    /// 
    ///     try
    ///     {
    ///         DoSomethingElse(null);
    ///     }
    ///     catch(Exception ex)
    ///     {
    ///         SimpleLog.Log(ex);
    ///         throw new InvalidOperationException("Something went wrong.", ex);
    ///     }            
    /// }
    /// 
    /// private static void DoSomethingElse(string fred)
    /// {
    ///     SimpleLog.Info("Entering method. See Source which method is meant.");
    /// 
    ///     try
    ///     {
    ///         // Purposely provoking an exception.
    ///         int a = fred.IndexOf("Hello");
    ///     }
    ///     catch(Exception ex)
    ///     {
    ///         throw new Exception("Something went wrong.", ex);
    ///     }                      
    /// }
    /// </code>
    /// </example>
    public static class SimpleLog
    {
        /// <summary>
        /// Log entry queue
        /// </summary>
        private static readonly Queue<XElement> LogEntryQueue = new Queue<XElement>();

        /// <summary>
        /// Snyc root for the background task itself
        /// </summary>
        private static readonly object BackgroundTaskSyncRoot = new object();

        /// <summary>
        /// Snyc root for the log file
        /// </summary>
        private static readonly object LogFileSyncRoot = new object();

        /// <summary>
        /// Directory to log to
        /// </summary>
        /// <remarks>
        /// Default is the application's current working directory
        /// </remarks>
        private static DirectoryInfo logDir = new DirectoryInfo(Directory.GetCurrentDirectory());

        /// <summary>
        /// Prefix to use in file name
        /// </summary>
        /// <remarks>
        /// Default is the empty string, i.e. no prefix.
        /// </remarks>
        private static string prefix;

        /// <summary>
        /// Date format to use in file name
        /// </summary>
        /// <remarks>
        /// Default is "yyyy_MM_dd" (e.g. 2013_04_21), which leads to a daily change of the log file.
        /// </remarks>
        private static string dateFormat;

        /// <summary>
        /// Suffix to use in file name
        /// </summary>
        /// <remarks>
        /// Default is the empty string, i.e. no suffix.
        /// </remarks>
        private static string suffix;

        /// <summary>
        /// Extension to use in file name
        /// </summary>
        /// <remarks>
        /// Default is "log".
        /// </remarks>
        private static string extension;

        /// <summary>
        /// Background task to write log entries to disk
        /// </summary>
        private static Task backgroundTask;

        /// <summary>
        /// Backing field for <see cref="TextSeparator"/>.
        /// </summary>
        private static string textSeparator = " | ";

        /// <summary>
        /// Initializes static members of the <see cref="SimpleLog"/> class.  
        /// </summary>
        static SimpleLog()
        {
            // Attach to process exit event
            AppDomain.CurrentDomain.ProcessExit += CurrentDomainProcessExit;
        }

        /// <summary>
        /// Log severity
        /// </summary>
        public enum Severity
        {
            Info,
            Warning,
            Error,
            Exception
        }

        /// <summary>
        /// Gets Directory to log to
        /// </summary>
        /// <remarks>
        /// Default is the application's current working directory. Can be set using <see cref="SetLogDir"/>.
        /// Log file is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string LogDir => logDir.FullName;

        /// <summary>
        /// Gets or sets the Prefix to use in file name
        /// </summary>
        /// <remarks>
        /// Default is the empty string, i.e. no prefix.
        /// Log file is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string Prefix
        {
            get => prefix ?? string.Empty;
            set => prefix = value;
        }

        /// <summary>
        /// Gets or sets the Suffix to use in file name
        /// </summary>
        /// <remarks>
        /// Default is the empty string, i.e. no suffix.
        /// Log file is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string Suffix
        {
            get => suffix ?? string.Empty;
            set => suffix = value;
        }

        /// <summary>
        /// Gets or sets the Extension to use in file name
        /// </summary>
        /// <remarks>
        /// Default is "log". Set to null to return to default.
        /// Log file is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string Extension
        {
            get => extension ?? "log";
            set => extension = value;
        }

        /// <summary>
        /// Gets or sets the Date format to use in file name
        /// </summary>
        /// <remarks>
        /// Default is "yyyy_MM_dd" (e.g. 2013_04_21), which leads to a daily change of the log file. Set to null to return to default. Set to e.g. "yyyy_MM_dd_HH" to change log file hourly.
        /// Log file is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string DateFormat
        {
            get => dateFormat ?? "yyyy_MM_dd";
            set => dateFormat = value;
        }

        /// <summary>
        /// Gets or sets the Log level
        /// </summary>
        /// <remarks>
        /// Log all entries with <see cref="Severity"/> set here and above. In other words, do not write entries to the log file with 
        /// severity below the severity specified here.
        /// 
        /// For example, when log level is set to <see cref="Severity.Info"/>, incoming entries with severity
        /// <see cref="Severity.Info"/>, <see cref="Severity.Warning"/>, <see cref="Severity.Error"/> and <see cref="Severity.Exception"/> 
        /// are actually written to the log file. When log level is set to e.g. <see cref="Severity.Error"/>, only 
        /// entries with severity <see cref="Severity.Error"/> and <see cref="Severity.Exception"/> are actually written to the log file. 
        /// Default is <see cref="Severity.Info"/>. <see cref="Log(XElement, Severity, bool, int)"/> for details.
        /// </remarks>
        public static Severity LogLevel { get; set; } = Severity.Info;

        /// <summary>
        /// Gets or sets a value indicating whether logging has to be started explicitly as opposed to start automatically on first log. Default is false.
        /// </summary>
        /// <remarks>
        /// Normally, logging starts automatically when the first log entry is enqueued, <see cref="Enqueue"/>. In some 
        /// situations, it may be desired to start logging explicitly at a later time. In the meantime, logging 
        /// entries are enqued and are processed (i.e. written to the log file) when logging is started. 
        /// To start logging, use <see cref="StartLogging"/>
        /// </remarks>
        public static bool StartExplicitly
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a value indicating whether to write plain text instead of XML. Default is false.
        /// </summary>
        public static bool WriteText
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the separator text entries reperesenting attributes or values are separated with, when <see cref="WriteText"/> is true. Defaults to " | ".
        /// </summary>
        public static string TextSeparator
        {
            get => textSeparator;
            set => textSeparator = value ?? string.Empty;
        }

        /// <summary>
        /// Gets file to log into
        /// </summary>
        /// <remarks>
        /// Is assembled from <see cref="LogDir"/>, <see cref="Prefix"/>, the current date and time formatted in <see cref="DateFormat"/>, 
        /// <see cref="Suffix"/>, "." and <see cref="Extension"/>. So, by default, the file is named e.g. "2013_04_21.log" and is written to the current working directory.
        /// It is assembled in <see cref="GetFileName"/> using <code>string.Format("{0}\\{1}{2}{3}.{4}", LogDir, Prefix, dateTime.ToString(DateFormat), Suffix, Extension)</code>.
        /// </remarks>
        public static string FileName => GetFileName(DateTime.Now);

        /// <summary>
        /// Gets a value indicating whether to stop enqueing new entries.
        /// </summary>
        /// <remarks>
        /// Use <see cref="StopLogging"/> to stop logging and <see cref="StartLogging"/> to start logging.
        /// </remarks>
        public static bool StopEnqueingNewEntries
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets a value indicating whether to stop logging background task is requested, i.e. to stop logging at all is requested.
        /// </summary>
        /// <remarks>
        /// Use <see cref="StopLogging"/> to stop logging and <see cref="StartLogging"/> to start logging.
        /// </remarks>
        public static bool StopLoggingRequested
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the last exception that occurred in the background task when trying to write to the file.
        /// </summary>
        public static Exception LastExceptionInBackgroundTask
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of log entries waiting to be written to file
        /// </summary>
        /// <remarks>
        /// When this number is 1000 or more, there seems to be a permanent problem to wite 
        /// to the file. See <see cref="LastExceptionInBackgroundTask"/> what it could be.
        /// </remarks>
        public static int NumberOfLogEntriesWaitingToBeWrittenToFile => LogEntryQueue.Count;

        /// <summary>
        /// Gets a value indicating whether logging background task currenty runs, i.e. 
        /// log entries are written to disk.
        /// </summary>
        /// <remarks>
        /// If logging is not running (yet), log methods can be called anyway. Messages will 
        /// be written to disk when logging is started. See <see cref="Enqueue"/> for details.
        /// </remarks>
        public static bool LoggingStarted => backgroundTask != null;

        /// <summary>
        /// Gets or sets a value indicating whether background task logging is disabled, 
        /// and thus immediate logging is executed.
        /// </summary>
        public static bool BackgroundTaskDisabled { get; set; } = false;

        /// <summary>
        /// Set all log properties at once
        /// </summary>
        /// <remarks>
        /// Set all log customizing properties at once. This is a pure convenience function. All parameters are optional.
        /// When <see cref="logDir"/> is set and it cannot be created or writing a first entry fails, no exception is thrown, but the previous directory, 
        /// respectively the default directory (the current working directory), is used instead.
        /// </remarks>
        /// <param name="updatedLogDirectory"><see cref="LogDir"/> for details. When null is passed here, <see cref="LogDir"/> is not set. Here, <see cref="LogDir"/> is created, when it does not exist.</param>
        /// <param name="updatedPrefix"><see cref="Prefix"/> for details. When null is passed here, <see cref="Prefix"/> is not set.</param>
        /// <param name="updatedSuffix"><see cref="Suffix"/> for details. When null is passed here, <see cref="Suffix"/> is not set.</param>
        /// <param name="updatedExtension"><see cref="Extension"/> for details. When null is passed here, <see cref="Extension"/> is not set.</param>
        /// <param name="updatedDateFormat"><see cref="DateFormat"/> for details. When null is passed here, <see cref="DateFormat"/> is not set.</param>
        /// <param name="logLevel"><see cref="LogLevel"/> for details. When null is passed here, <see cref="LogLevel"/> is not set.</param>
        /// <param name="startExplicitly"><see cref="StartExplicitly"/> for details. When null is passed here, <see cref="StartExplicitly"/> is not set.</param>
        /// <param name="check">Whether to call <see cref="Check"/>, i.e. whether to write a test entry after setting the new log file. If true, the result of <see cref="Check"/> is returned.</param>
        /// <param name="writeText"><see cref="WriteText"/> for details. When null is passed here, <see cref="WriteText"/> is not set.</param>
        /// <param name="updatedTextSeparator"><see cref="TextSeparator"/> for details. When null is passed here, <see cref="TextSeparator"/> is not set.</param>
        /// <returns>Null on success, otherwise an exception with what went wrong.</returns>
        public static Exception SetLogFile(string updatedLogDirectory = null, string updatedPrefix = null, string updatedSuffix = null, string updatedExtension = null, string updatedDateFormat = null, Severity? logLevel = null, bool? startExplicitly = null, bool check = true, bool? writeText = null, string updatedTextSeparator = null)
        {
            Exception result = null;

            try
            {
                if (writeText != null)
                {
                    WriteText = writeText.Value;
                }

                if (updatedTextSeparator != null)
                {
                    TextSeparator = updatedTextSeparator;
                }

                if (logLevel != null)
                {
                    LogLevel = logLevel.Value;
                }

                if (updatedExtension != null)
                {
                    Extension = updatedExtension;
                }

                if (updatedSuffix != null)
                {
                    Suffix = updatedSuffix;
                }

                if (updatedDateFormat != null)
                {
                    DateFormat = updatedDateFormat;
                }

                if (updatedPrefix != null)
                {
                    Prefix = updatedPrefix;
                }

                if (startExplicitly != null)
                {
                    StartExplicitly = startExplicitly.Value;
                }

                if (updatedLogDirectory != null)
                {
                    result = SetLogDir(updatedLogDirectory, true);
                }

                // Check if logging works with new settings
                if (result == null && check)
                {
                    result = Check();
                }
            }
            catch (Exception ex)
            {
                result = ex;
            }

            return result;
        }

        /// <summary>
        /// Set new logging directory
        /// </summary>
        /// <param name="logDirectory">The logging diretory to set. When passing null or the empty string, the current working directory is used.</param>
        /// <param name="createIfNotExisting">Try to create directory if not existing. Default is false.</param>
        /// <returns>Null if setting log directory was successful, otherwise an exception with what went wrong.</returns>
        public static Exception SetLogDir(string logDirectory, bool createIfNotExisting = false)
        {
            if (string.IsNullOrEmpty(logDirectory))
            {
                logDirectory = Directory.GetCurrentDirectory();
            }

            try
            {
                logDir = new DirectoryInfo(logDirectory);

                if (!logDir.Exists)
                {
                    if (createIfNotExisting)
                    {
                        logDir.Create();
                    }
                    else
                    {
                        throw new DirectoryNotFoundException($"Directory '{logDir.FullName}' does not exist!");
                    }
                }
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }

        /// <summary>
        /// Check if logging to <see cref="FileName"/> works
        /// </summary>
        /// <remarks>
        /// Writes a test entry directly to <see cref="FileName"/> without using the background task. 
        /// When no exception is returned, logging to <see cref="FileName"/> works.
        /// </remarks>
        /// <param name="message">Test message to write to the log file</param>
        /// <returns>Null on success, otherwise an exception with what went wrong.</returns>
        public static Exception Check(string message = "Test entry to see if logging works.")
        {
            // Try to write directly to the file to see if it's working.
            return Log(message, Severity.Info, false);
        }

        /// <summary>
        /// Write info message to log
        /// </summary>
        /// <param name="message">The message to write to the log</param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Info(string message, bool useBackgroundTask = true)
        {
            return Log(message, Severity.Info, useBackgroundTask);
        }

        /// <summary>
        /// Write warning message to log
        /// </summary>
        /// <param name="message">The message to write to the log</param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Warning(string message, bool useBackgroundTask = true)
        {
            return Log(message, Severity.Warning, useBackgroundTask);
        }

        /// <summary>
        /// Write error message to log
        /// </summary>
        /// <param name="message">The message to write to the log</param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Error(string message, bool useBackgroundTask = true)
        {
            return Log(message, Severity.Error, useBackgroundTask);
        }

        /// <summary>
        /// Write exception to log
        /// </summary>
        /// <param name="ex">The exception to write to the log</param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <param name="framesToSkip">How many frames to skip when detecting the calling method, <see cref="GetCaller"/>. This is useful when log calls to <see cref="SimpleLog"/> are wrapped in an application. Default is 0.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Log(Exception ex, bool useBackgroundTask = true, int framesToSkip = 0)
        {
            return ex == null ? null : Log(GetExceptionXElement(ex), Severity.Exception, useBackgroundTask, framesToSkip);
        }

        /// <summary>
        /// Gets an XML string with detailed information about an exception
        /// </summary>
        /// <remarks>
        /// Recursively adds elements for inner exceptions. For the most inner exception, the stack trace is added.
        /// Tags for <see cref="Exception.Data"/> are added. Specific properties of the exception types <see cref="SqlException"/>,
        /// <see cref="COMException"/> and <see cref="AggregateException"/> are recognized, too.
        /// </remarks>
        /// <param name="ex">The exception to get detailed information about</param>
        /// <returns>An XML string with detailed information about the passed exception</returns>
        public static string GetExceptionAsXmlString(Exception ex)
        {
            var xElement = GetExceptionXElement(ex);
            return xElement?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Gets an XElement for an exception
        /// </summary>
        /// <remarks>
        /// Recursively adds elements for inner exceptions. For the most inner exception, the stack trace is added.
        /// Tags for <see cref="Exception.Data"/> are added. Specific properties of the exception types <see cref="SqlException"/>,
        /// <see cref="COMException"/> and <see cref="AggregateException"/> are recognized, too.
        /// </remarks>
        /// <param name="ex">The exception to get the XElement for</param>
        /// <returns>An XElement for the exception</returns>
        public static XElement GetExceptionXElement(Exception ex)
        {
            if (ex == null)
            {
                return null;
            }

            var xElement = new XElement("Exception");
            xElement.Add(new XAttribute("Type", ex.GetType().FullName ?? "unknown"));
            xElement.Add(new XAttribute("Source", ex.TargetSite == null || ex.TargetSite.DeclaringType == null ? ex.Source : $"{ex.TargetSite.DeclaringType.FullName}.{ex.TargetSite.Name}"));
            xElement.Add(new XElement("Message", ex.Message));

            if (ex.Data.Count > 0)
            {
                var xDataElement = new XElement("Data");

                foreach (DictionaryEntry de in ex.Data)
                {
                    xDataElement.Add(new XElement("Entry", new XAttribute("Key", de.Key), new XAttribute("Value", de.Value ?? string.Empty)));
                }

                xElement.Add(xDataElement);
            }

            switch (ex)
            {
                case SqlException sqlEx:
                    var xSqlElement = new XElement("SqlException");
                    xSqlElement.Add(new XAttribute("ErrorNumber", sqlEx.Number));

                    if (!string.IsNullOrEmpty(sqlEx.Server))
                    {
                        xSqlElement.Add(new XAttribute("ServerName", sqlEx.Server));
                    }

                    if (!string.IsNullOrEmpty(sqlEx.Procedure))
                    {
                        xSqlElement.Add(new XAttribute("Procedure", sqlEx.Procedure));
                    }

                    xElement.Add(xSqlElement);
                    break;
                case COMException comEx:
                    var xComElement = new XElement("ComException");
                    xComElement.Add(new XAttribute("ErrorCode", $"0x{(uint)comEx.ErrorCode:X8}"));
                    xElement.Add(xComElement);
                    break;
                case AggregateException exception:
                    var xAggElement = new XElement("AggregateException");
                    foreach (var innerEx in exception.InnerExceptions)
                    {
                        xAggElement.Add(GetExceptionXElement(innerEx));
                    }

                    xElement.Add(xAggElement);
                    break;
            }

            xElement.Add(ex.InnerException == null ? new XElement("StackTrace", ex.StackTrace) : GetExceptionXElement(ex.InnerException));

            return xElement;
        }

        /// <summary>
        /// Write message to log
        /// </summary>
        /// <remarks>
        /// See <see cref="Log(XElement, Severity, bool, int)"/>.
        /// </remarks>
        /// <param name="message">The message to write to the log</param>
        /// <param name="severity">Log entry severity</param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <param name="framesToSkip">How many frames to skip when detecting the calling method, <see cref="GetCaller"/>. This is useful when log calls to <see cref="SimpleLog"/> are wrapped in an application. Default is 0.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Log(string message, Severity severity = Severity.Info, bool useBackgroundTask = true, int framesToSkip = 0)
        {
            return string.IsNullOrEmpty(message) ? null : Log(new XElement("Message", message), severity, useBackgroundTask, framesToSkip);
        }

        /// <summary>
        /// Write XElement to log
        /// </summary>
        /// <remarks>
        /// Unless <paramref name="useBackgroundTask"/> is set to false (default is true), the XElement is not actually 
        /// written to the file here, but enqueued to the log entry queue. It is dequeued by 
        /// <see cref="WriteLogEntriesToFile"/> in a backround task and actually written to the file there. 
        /// This is much faster than writing directly to disk in the main thread (what is done when 
        /// <paramref name="useBackgroundTask"/> is set to false). 
        /// 
        /// However, writing to the file is synchronized between threads. I.e. writing directly can be done from multiple threads. 
        /// Also, using the background task and writing directly to the file can be used both in parallel.
        /// 
        /// When <see cref="StartExplicitly"/> is set to true (default is false), the background task must be started 
        /// explicitly by calling <see cref="StartLogging"/>, to get messages actually written to the file. They get enqueued 
        /// before the background task is started, though. I.e. they will get logged when the background task is started later.
        /// 
        /// When <see cref="StartExplicitly"/> is set to false, which is the default, logging background task (thread) is 
        /// started automatically when first calling this method with <paramref name="useBackgroundTask"/> set to true 
        /// (which is the default).
        /// </remarks>
        /// <param name="xElement">The XElement to log</param>
        /// <param name="severity">Log entry severity, defaults to <see cref="Severity.Info"/></param>
        /// <param name="useBackgroundTask">Whether to use the background task (thread) to write messages to disk. Default is true. This is much faster than writing directly to disk in the main thread.</param>
        /// <param name="framesToSkip">How many frames to skip when detecting the calling method, <see cref="GetCaller"/>. This is useful when log calls to <see cref="SimpleLog"/> are wrapped in an application. Default is 0.</param>
        /// <returns>Null on success or the <see cref="Exception"/> that occurred when processing the message, i.e. when enqueuing the message (when <paramref name="useBackgroundTask"/> is true) or when writing the message to disk (when <paramref name="useBackgroundTask"/> is false).</returns>
        public static Exception Log(XElement xElement, Severity severity = Severity.Info, bool useBackgroundTask = true, int framesToSkip = 0)
        {
            // Filter entries below log level
            if (xElement == null)
            {
                return null;
            }

            try
            {
                // Assemble XML log entry
                var logEntry = new XElement("LogEntry");
                logEntry.Add(new XAttribute("Date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
                logEntry.Add(new XAttribute("Severity", severity));
                logEntry.Add(new XAttribute("Source", GetCaller(framesToSkip)));
                logEntry.Add(new XAttribute("ThreadId", Thread.CurrentThread.ManagedThreadId));
                logEntry.Add(xElement);

                if (useBackgroundTask && !BackgroundTaskDisabled)
                {
                    // Enqueue log entry to be written to the file by background task
                    Enqueue(logEntry);
                }
                else
                {
                    // Write directly to the file. This is synchronized among threads within the method,
                    // so can be used in parallel with the above.
                    return WriteLogEntryToFile(logEntry);
                }
            }
            catch (Exception ex)
            {
                return ex;
            }

            return null;
        }

        /// <summary>
        /// Gets the log filename for the passed date
        /// </summary>
        /// <param name="dateTime">The date to get the log file name for</param>
        /// <returns>The log filename for the passed date</returns>
        public static string GetFileName(DateTime dateTime)
        {
            return $"{LogDir}\\{Prefix}{dateTime.ToString(DateFormat)}{Suffix}.{Extension}";
        }

        /// <summary>
        /// Check, whether there is a log file for the passed date
        /// </summary>
        /// <param name="dateTime">The date and time to check the existance of a log file for</param>
        /// <returns>True = log file exists, false otherwise</returns>
        public static bool LogFileExists(DateTime dateTime)
        {
            return File.Exists(GetFileName(dateTime));
        }

        /// <summary>
        /// Get the current log file as XML document
        /// </summary>
        /// <remarks>
        /// Does not throw an exception when the log file does not exist.
        /// </remarks>
        /// <returns>The log file as XML document or null when it does not exist.</returns>
        public static XDocument GetLogFileAsXml()
        {
            return GetLogFileAsXml(DateTime.Now);
        }

        /// <summary>
        /// Get the log file for the passed date as XML document
        /// </summary>
        /// <remarks>
        /// Does not throw an exception when the log file does not exist.
        /// </remarks>
        /// <param name="dateTime">The date and time to get the log file for. Use DateTime.Now to get the current log file.</param>
        /// <returns>The log file as XML document or null when it does not exist.</returns>
        public static XDocument GetLogFileAsXml(DateTime dateTime)
        {
            var fileName = GetFileName(dateTime);
            if (!File.Exists(fileName))
            {
                return null;
            }

            Flush();

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<LogEntries>");
            sb.AppendLine(File.ReadAllText(fileName));
            sb.AppendLine("</LogEntries>");

            return XDocument.Parse(sb.ToString());
        }

        /// <summary>
        /// Get the current log file as text document
        /// </summary>
        /// <remarks>
        /// Does not throw an exception when the log file does not exist.
        /// </remarks>
        /// <returns>The log file as text document or null when it does not exist.</returns>
        public static string GetLogFileAsText()
        {
            return GetLogFileAsText(DateTime.Now);
        }

        /// <summary>
        /// Get the log file for the passed date as text document
        /// </summary>
        /// <remarks>
        /// Does not throw an exception when the log file does not exist.
        /// </remarks>
        /// <param name="dateTime">The date and time to get the log file for. Use DateTime.Now to get the current log file.</param>
        /// <returns>The log file as text document or null when it does not exist.</returns>
        public static string GetLogFileAsText(DateTime dateTime)
        {
            var fileName = GetFileName(dateTime);
            if (!File.Exists(fileName))
            {
                return null;
            }

            Flush();

            return File.ReadAllText(fileName);
        }

        /// <summary>
        /// Shows the current log file 
        /// </summary>
        /// <remarks>
        /// Opens the default program to show text or XML files and displays the requested file, if it exists. Does nothing otherwise.
        /// When <see cref="WriteText"/> is false, a temporary XML file is created and saved in the users's temporary path each time this method is called. 
        /// So don't use it excessively in that case. Otherwise, the log file itself is shown.
        /// </remarks>
        public static void ShowLogFile()
        {
            ShowLogFile(DateTime.Now);
        }

        /// <summary>
        /// Show a log file for the passed date
        /// </summary>
        /// <remarks>
        /// Opens the default program to show text or XML files and displays the requested file, if it exists. Does nothing otherwise.
        /// When <see cref="WriteText"/> is false, a temporary XML file is created and saved in the users's temporary path each time this method is called. 
        /// So don't use it excessively in that case. Otherwise, the log file itself is shown.
        /// </remarks>
        /// <param name="dateTime">The date and time to show the log file for.</param>
        public static void ShowLogFile(DateTime dateTime)
        {
            string fileName;

            if (WriteText)
            {
                Flush();
                fileName = GetFileName(dateTime);
            }
            else
            {
                fileName = $"{Path.GetTempPath()}Log_{DateTime.Now:yyyyMMddHHmmssffff}.xml";
                var logFileXml = GetLogFileAsXml(dateTime);
                logFileXml?.Save(fileName);
            }

            if (!File.Exists(fileName))
            {
                return;
            }

            // Let system choose application to start
            Process.Start(fileName);

            // Wait a little to give application time to open
            Thread.Sleep(2000);
        }

        /// <summary>
        /// Start logging
        /// </summary>
        /// <remarks>
        /// Start background task pointing to <see cref="WriteLogEntriesToFile"/> to write log files to disk. 
        /// Is called automatically by <see cref="Enqueue"/> when the first entry is logged, unless
        /// <see cref="StartExplicitly"/> is set to true (default is false). Then, this method has to be 
        /// called explicitly to start logging.
        /// </remarks>
        public static void StartLogging()
        {
            // Task already started
            if (backgroundTask != null || StopEnqueingNewEntries || StopLoggingRequested)
            {
                return;
            }

            // Reset stopping flags
            StopEnqueingNewEntries = false;
            StopLoggingRequested = false;

            lock (BackgroundTaskSyncRoot)
            {
                if (backgroundTask != null)
                {
                    return;
                }

                // Reset last exception
                LastExceptionInBackgroundTask = null;

                // Create and start task
                backgroundTask = new Task(WriteLogEntriesToFile, TaskCreationOptions.LongRunning);
                backgroundTask.Start();
            }
        }

        /// <summary>
        /// Stop logging background task, i.e. logging at all.
        /// </summary>
        /// <remarks>
        /// Stop background task pointing to <see cref="WriteLogEntriesToFile"/> to write log files to disk. 
        /// </remarks>
        /// <param name="flush">Whether to write all pending entries to disk before. Default is true.</param>
        public static void StopLogging(bool flush = true)
        {
            // Stop enqueing new log entries.
            StopEnqueingNewEntries = true;

            // Useless to go on ...
            if (backgroundTask == null)
            {
                return;
            }

            // Write pending entries to disk.
            if (flush)
            {
                Flush();
            }

            // Now tell the background task to stop.
            StopLoggingRequested = true;

            lock (BackgroundTaskSyncRoot)
            {
                if (backgroundTask == null)
                {
                    return;
                }

                // Wait for task to finish and set null then.
                backgroundTask.Wait(1000);
                backgroundTask = null;
            }
        }

        /// <summary>
        /// Wait for all entries having been written to the file
        /// </summary>
        public static void Flush()
        {
            // Background task not running? Nothing to do.
            if (!LoggingStarted)
            {
                return;
            }

            // Are there still items waiting to be written to disk?
            while (NumberOfLogEntriesWaitingToBeWrittenToFile > 0)
            {
                // Remember current number
                var lastNumber = NumberOfLogEntriesWaitingToBeWrittenToFile;

                // Wait some time to let background task do its work
                Thread.Sleep(222);

                // Didn't help? No log entries have been processed? We probably hang. 
                // Let it be to avoid waiting eternally.
                if (lastNumber == NumberOfLogEntriesWaitingToBeWrittenToFile)
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Clear background task's log entry queue. I.e. remove all log messages waiting to be written to <see cref="FileName"/> by the background task.
        /// </summary>
        public static void ClearQueue()
        {
            lock (LogEntryQueue)
            {
                LogEntryQueue.Clear();
            }
        }

        /// <summary>
        /// Process is about to exit
        /// </summary>
        /// <remarks>
        /// This is some kind of static destructor used to flush unwritten log entries.
        /// </remarks>
        /// <param name="sender">Event Sender</param>
        /// <param name="e">Event Args</param>
        private static void CurrentDomainProcessExit(object sender, EventArgs e)
        {
            StopLogging();
        }

        /// <summary>
        /// Enqueue log entry to be written to log file
        /// </summary>
        /// <remarks>
        /// When <see cref="StartExplicitly"/> is set to false (which is the default), 
        /// logging is started automatically by calling <see cref="StartLogging"/> from 
        /// inside this method when the first <paramref name="logEntry"/> is enqueued. 
        /// 
        /// When <see cref="StartExplicitly"/> is set to true, <paramref name="logEntry"/> 
        /// is just enqueued, but not yet actually written to the log file. 
        /// The latter will be done when <see cref="StartLogging"/> is called explicitly.
        /// </remarks>
        /// <param name="logEntry">The log entry to be enqueued</param>
        private static void Enqueue(XElement logEntry)
        {
            // Stop enqueuing when instructed to do so
            if (StopEnqueingNewEntries)
            {
                return;
            }

            // Start logging if not already started, unless it is desired to start it explicitly
            if (!StartExplicitly)
            {
                StartLogging();
            }

            lock (LogEntryQueue)
            {
                // Stop enqueueing when the queue gets too full.
                if (LogEntryQueue.Count < 10000)
                {
                    LogEntryQueue.Enqueue(logEntry);
                }
            }
        }

        /// <summary>
        /// Get the next log entry from the queue, but do not dequeue it
        /// </summary>
        /// <returns>The next element or null when the queue is empty</returns>
        private static XElement Peek()
        {
            lock (LogEntryQueue)
            {
                return LogEntryQueue.Count == 0 ? null : LogEntryQueue.Peek();
            }
        }

        /// <summary>
        /// Dequeue log entry
        /// </summary>
        private static void Dequeue()
        {
            lock (LogEntryQueue)
            {
                if (LogEntryQueue.Count > 0)
                {
                    LogEntryQueue.Dequeue();
                }
            }
        }

        /// <summary>
        /// Write log entries to the file on disk
        /// </summary>
        /// <remarks>
        /// The thread looks every 100 milliseconds for new items in the queue.
        /// </remarks>
        private static void WriteLogEntriesToFile()
        {
            while (!StopLoggingRequested)
            {
                // Get next log entry from queue
                var xmlEntry = Peek();
                if (xmlEntry == null)
                {
                    // If queue is empty, sleep for a while and look again later.
                    Thread.Sleep(100);
                    continue;
                }

                // Try ten times to write the entry to the log file. Wait between tries, because the file could (hopefully) temporarily 
                // be locked by another application. When it didn't work out after ten tries, dequeue the entry anyway, i.e. the entry is lost then. 
                // This is necessary to ensure that the queue does not get too full and we run out of memory.
                for (var i = 0; i < 10; i++)
                {
                    // Actually write entry to log file.
                    var ex = WriteLogEntryToFile(xmlEntry);
                    WriteOwnExceptionToEventLog(ex);
                    LastExceptionInBackgroundTask = ex;

                    // When all is fine, we're done. Otherwise do not retry when queue is already getting full.
                    if (LastExceptionInBackgroundTask == null || NumberOfLogEntriesWaitingToBeWrittenToFile > 1000)
                    {
                        break;
                    }

                    // Only wait when queue is not already getting full.
                    Thread.Sleep(100);
                }

                // Dequeue entry from the queue
                Dequeue();
            }
        }

        /// <summary>
        /// Write exceptions happening here, i.e. <see cref="LastExceptionInBackgroundTask"/> to the event log.
        /// </summary>
        /// <remarks>
        /// When there are exceptions occurring when we try to write to disk, sometimes it is hard to find out why this fails.
        /// That's why they are written to the event log here. For not to clutter up event log too much, doubles are sorted out.
        /// The event is written as an error to the application event log under source "SimpleLog".
        /// </remarks>
        /// <param name="ex">The exception to write to the event log.</param>
        private static void WriteOwnExceptionToEventLog(Exception ex)
        {
            // Filter out doubles for not to clutter up exception log.
            if (ex == null || (LastExceptionInBackgroundTask != null && ex.Message == LastExceptionInBackgroundTask.Message))
            {
                return;
            }

            try
            {
                const string Source = "SimpleLog";
                const string LogName = "Application";
                string message;

                try
                {
                    var xElement = GetExceptionXElement(ex);
                    message = xElement.ToString();
                }
                catch
                {
                    message = ex.Message;
                }

                if (!EventLog.SourceExists(Source))
                {
                    EventLog.CreateEventSource(Source, LogName);
                }

                EventLog.WriteEntry(Source, message, EventLogEntryType.Error, 0);
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Write one log entry to file
        /// </summary>
        /// <remarks>
        /// This method can be called from the logging background thread or directly 
        /// from the main thread. Lock accordingly to avoid multiple threads concurrently 
        /// accessing the file. When the lock can not be got within five seconds, 
        /// <paramref name="xmlEntry" /> is not being written to the file, but a respective 
        /// exception is returned, saying what went wrong.
        /// </remarks>
        /// <param name="xmlEntry">The entry to write</param>
        /// <returns>Null when all worked fine, an exception otherwise</returns>
        private static Exception WriteLogEntryToFile(XElement xmlEntry)
        {
            if (xmlEntry == null)
            {
                return null;
            }

            const int SecondsToWaitForFile = 5;

            // This method can be called from the logging background thread or directly 
            // from the main thread. Lock accordingly to avoid multiple threads concurrently 
            // accessing the file.
            if (Monitor.TryEnter(LogFileSyncRoot, new TimeSpan(0, 0, 0, SecondsToWaitForFile)))
            {
                try
                {
                    // Use filestream to be able to explicitly specify FileShare.None
                    using (var fileStream = new FileStream(FileName, FileMode.Append, FileAccess.Write, FileShare.None))
                    {
                        using (var streamWriter = new StreamWriter(fileStream))
                        {
                            if (WriteText)
                            {
                                // Write plain text
                                streamWriter.WriteLine(ConvertXmlToPlainText(xmlEntry));
                            }
                            else
                            {
                                // Write XML
                                streamWriter.WriteLine(xmlEntry);
                            }
                        }
                    }

                    return null;
                }
                catch (Exception ex)
                {
                    try
                    {
                        ex.Data["Filename"] = FileName;
                    }
                    catch
                    {
                        // ignored
                    }

                    try
                    {
                        var user = WindowsIdentity.GetCurrent();
                        ex.Data["Username"] = user.Name;
                    }
                    catch
                    {
                        // ignored
                    }

                    return ex;
                }
                finally
                {
                    Monitor.Exit(LogFileSyncRoot);
                }
            }

            try
            {
                return new Exception(
                    $"Could not write to file '{FileName}', because it was blocked by another thread for more than {SecondsToWaitForFile} seconds.");
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        /// <summary>
        /// Convert <paramref name="xmlEntry"/> to plain text to be written to a file.
        /// </summary>
        /// <remarks>
        /// A typical xml entry to be converted looks like this:
        /// <![CDATA[
        /// 
        /// <LogEntry Date="2014-06-19 11:20:52" Severity="Info" Source="SimpleLogDemo.Program.DoSomethingElse" ThreadId="9">
        ///   <Message>Entering method. See Source which method is meant.</Message>
        /// </LogEntry>
        /// <LogEntry Date="2014-06-19 11:20:52" Severity="Exception" Source="SimpleLogDemo.Program.DoSomething" ThreadId="9">
        ///   <Exception Type="System.Exception" Source="SimpleLogDemo.Program.DoSomethingElse">
        ///     <Message>Something went wrong.</Message>
        ///     <Exception Type="System.NullReferenceException" Source="SimpleLogDemo.Program.DoSomethingElse">
        ///       <Message>Object reference not set to an instance of an object.</Message>
        ///       <StackTrace>   at SimpleLogDemo.Program.DoSomethingElse(String fred) in D:\Projekt\VisualStudio\SimpleLogDemo\SimpleLogDemo\Program.cs:line 91</StackTrace>
        ///     </Exception>
        ///   </Exception>
        /// </LogEntry>
        /// 
        /// ]]>
        /// 
        /// This is a basic implementation so far. Feel free to implement your own if you need something more sophisticated, e.g. 
        /// nicer exception formatting.
        /// </remarks>
        /// <param name="xmlEntry">The XML entry to convert.</param>
        /// <returns><paramref name="xmlEntry"/> converted to plain text.</returns>
        private static string ConvertXmlToPlainText(XElement xmlEntry)
        {
            var sb = new StringBuilder();

            foreach (var element in xmlEntry.DescendantsAndSelf())
            {
                if (element.HasAttributes)
                {
                    foreach (var attribute in element.Attributes())
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(TextSeparator);
                        }

                        sb.Append(attribute.Name).Append(" = ").Append(attribute.Value);
                    }
                }
                else
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(TextSeparator);
                    }

                    // Remove new lines to get all in one line.
                    var value = element.Value.Replace("\r\n", " ");
                    sb.Append(element.Name).Append(" = ").Append(value);
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Detects the method that was calling the log method
        /// </summary>
        /// <remarks>
        /// The method is walking up the frames in the stack trace until the first method outside <see cref="SimpleLog"/> is reached.
        /// When log calls to <see cref="SimpleLog"/> are wrapped in an application, this may still not be the method where logging 
        /// was called initially (e.g. when an exception occurred and has been logged). In that case set <paramref name="framesToSkip"/> 
        /// accordingly to get outside the wrapper method(s).
        /// </remarks>
        /// <param name="framesToSkip">How many frames to skip when detecting the calling method. This is useful when log calls to <see cref="SimpleLog"/> are wrapped in an application. Default is 0.</param>
        /// <returns>Class and method that was calling the log method</returns>
        private static string GetCaller(int framesToSkip = 0)
        {
            var result = string.Empty;

            var i = 1;

            while (true)
            {
                // Walk up the stack trace ...
                var stackFrame = new StackFrame(i++);
                var methodBase = stackFrame.GetMethod();
                if (methodBase == null)
                {
                    break;
                }

                // Here we're at the end - nomally we should never get that far 
                var declaringType = methodBase.DeclaringType;
                if (declaringType == null)
                {
                    break;
                }

                // Get class name and method of the current stack frame
                result = $"{declaringType.FullName}.{methodBase.Name}";

                // Here, we're at the first method outside of SimpleLog class. 
                // This is the method that called the log method. We're done unless it is 
                // specified to skip additional frames and go further up the stack trace.
                if (declaringType != typeof(SimpleLog) && --framesToSkip < 0)
                {
                    break;
                }
            }

            return result;
        }
    }
}
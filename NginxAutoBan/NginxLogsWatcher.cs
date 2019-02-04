using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NAB
{
    class NginxLogsWatcher : IDisposable
    {
        private class StreamingContext
        {
            private ILogger logger = Log.ForContext<StreamingContext>();
            private StringBuilder pendingLine = new StringBuilder();
            private long streamPosition = 0;
            private String filePath;

            public StreamingContext(String filePath)
            {
                this.filePath = filePath;
                Rename(filePath);
            }

            public void Rename(String filePath)
            {
                this.filePath = filePath;
            }
            
            public IEnumerable<String> ReadPendingText(CancellationToken cancellationToken)
            {
                using (var fs = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    fs.Position = Math.Min(streamPosition, fs.Length);

                    GZipStream gzs = null;
                    StreamReader sr = null;
                    if (filePath.EndsWith(".gz"))
                    {
                        gzs = new GZipStream(fs, CompressionMode.Decompress, true);
                        sr = new StreamReader(gzs);
                    }
                    else
                    {
                        sr = new StreamReader(fs);
                    }

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        String newLine = null;
                        try
                        {
                            if (sr.EndOfStream)
                                break;

                            char c = (char)sr.Read();
                            if (c == '\n' || c == '\r')
                            {
                                if (pendingLine.Length > 0)
                                {
                                    newLine = pendingLine.ToString();
                                    logger.Verbose("Got line: {line}", newLine);
                                    pendingLine.Clear();
                                }
                            }
                            else
                            {
                                pendingLine.Append(c);
                            }
                        }
                        catch (Exception e)
                        {
                            logger.Warning("Exception occurred reading pending text: {message}", e.Message);
                            break;
                        }

                        if (newLine != null)
                            yield return newLine;
                    }

                    streamPosition = fs.Position;

                    sr?.Dispose();
                    gzs?.Dispose();
                }
            }
        }

        private class JobContext : IDisposable
        {
            NginxLogsWatcher target;
            public JobContext(NginxLogsWatcher target)
            {
                this.target = target;
                Interlocked.Increment(ref target.jobCounter);
            }

            public void Dispose()
            {
                Interlocked.Decrement(ref target.jobCounter);
            }
        }

        private FileSystemWatcher logFolderWatcher;
        private ILogger logger = Log.ForContext<NginxLogsWatcher>();
        private ConcurrentDictionary<String, StreamingContext> watchedFiles = new ConcurrentDictionary<string, StreamingContext>();
        private CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private int jobCounter = 0;

        private bool IsArchivedFile(String path) => path.ToLower().EndsWith(".gz");

        public NginxLogsWatcher(String logFolderPath)
        {
            logFolderPath = Path.GetFullPath(logFolderPath);
            logger.Debug("Getting existing log files from {path}...", logFolderPath);

            var searchPattern = "*.log*";

            this.logFolderWatcher = new FileSystemWatcher(logFolderPath)
            {
                IncludeSubdirectories = false,
                Filter = searchPattern,
                NotifyFilter =
                    NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Security
            };

            this.logFolderWatcher.Created += LogFolderWatcher_Created;
            this.logFolderWatcher.Deleted += LogFolderWatcher_Deleted;
            this.logFolderWatcher.Renamed += LogFolderWatcher_Renamed;
            this.logFolderWatcher.Changed += LogFolderWatcher_Changed;

            var existingFiles = Directory.EnumerateFiles(logFolderPath, searchPattern);
            foreach (var file in existingFiles)
            {
                var filePath = Path.Combine(logFolderPath, file);
                watchedFiles.TryAdd(filePath, new StreamingContext(filePath));
            }

            logger.Debug("Found {count} existing files", watchedFiles.Count);

            Task.Factory.StartNew(() => {
                using (new JobContext(this)) 
                    ReadWatchedFiles();
            }, TaskCreationOptions.LongRunning);
        }

        public delegate void NewLineHandler(String sourceFileName, String text);
        public event NewLineHandler NewLine;

        public void Dispose()
        {
            this.logFolderWatcher.EnableRaisingEvents = false;
            cancellationTokenSource.Cancel();
            while (jobCounter > 0)
                Task.Delay(10).Wait();

            this.logFolderWatcher.Dispose();
        }
        
        private void ReadWatchedFiles()
        {
            void ParsePendingText(String fileName, StreamingContext context)
            {
                foreach (var line in context.ReadPendingText(cancellationTokenSource.Token))
                    NewLine?.Invoke(fileName, line);
            }

            logger.Debug("Reading and parsing current file contents...");
            var readLineTasks = watchedFiles.Select(kvp =>
                Task.Run(() => ParsePendingText(kvp.Key, kvp.Value))
            ).ToList();

            using (new Profiler("Read initial file contents", logger))
            {
                var waitTask = Task.WhenAll(readLineTasks.ToArray());
                while (!waitTask.IsCompleted)
                    Task.Delay(10);
            }

            logger.Debug("Read tasks finished");

            if (cancellationTokenSource.IsCancellationRequested)
                return;

            var archiveFiles = watchedFiles.Keys.Where(IsArchivedFile).ToList();
            logger.Debug("Removing {count} archives from set of watched files...", archiveFiles.Count);
            foreach (var file in archiveFiles)
                watchedFiles.Remove(file, out StreamingContext _);

            using (new Profiler("Running GC for cleaned streams", logger))
                GC.Collect(2, GCCollectionMode.Forced, true, true);

            logger.Debug("Enabling FS watcher");
            this.logFolderWatcher.EnableRaisingEvents = true;
        }

        private void LogFolderWatcher_Created(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                if (IsArchivedFile(e.Name))
                    return;

                logger.Debug("File {name} created, watching...", e.Name);
                watchedFiles.TryAdd(e.FullPath, new StreamingContext(e.FullPath));
            }
        }

        private void LogFolderWatcher_Deleted(object sender, FileSystemEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File {name} deleted, removing from watch-list...", e.Name);
                try
                {
                    watchedFiles.TryRemove(e.FullPath, out StreamingContext _);
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception handling file delete ({type}): {message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        private void LogFolderWatcher_Renamed(object sender, RenamedEventArgs e)
        {
            lock (watchedFiles)
            {
                logger.Debug("File renamed {oldName} -> {newName}, reloading stream context...", e.OldName, e.Name);

                try
                {
                    if (watchedFiles.TryRemove(e.OldFullPath, out StreamingContext stream))
                    {
                        stream.Rename(e.FullPath);
                        watchedFiles.TryAdd(e.FullPath, stream);
                    }
                }
                catch (Exception ex)
                {
                    logger.Warning("Exception handling file rename ({type}): {message}", ex.GetType().Name, ex.Message);
                }
            }
        }

        void LogFolderWatcher_Changed(object sender, FileSystemEventArgs e)
        {
            logger.Verbose("File {name} changed, checking for updates...", e.Name);

            lock (watchedFiles)
                if (!watchedFiles.ContainsKey(e.FullPath))
                    return;

            var context = watchedFiles[e.FullPath];

            Task.Run(() =>
            {
                using (new JobContext(this))
                {
                    foreach (var line in context.ReadPendingText(cancellationTokenSource.Token))
                        NewLine?.Invoke(e.FullPath, line);
                }
            });
        }
    }
}

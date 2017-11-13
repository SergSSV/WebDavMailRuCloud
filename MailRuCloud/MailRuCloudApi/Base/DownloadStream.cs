﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Threading.Tasks;

namespace YaR.MailRuCloud.Api.Base
{
    public class DownloadStream : Stream
    {
        private static readonly log4net.ILog Logger = log4net.LogManager.GetLogger(typeof(DownloadStream));

        private const int InnerBufferSize = 65536;

        private readonly IList<File> _files;
        private ShardInfo _shard;
        private readonly CloudApi _cloud;
        private readonly long? _start;
        private readonly long? _end;

        private RingBufferedStream _innerStream;

        public DownloadStream(File file, CloudApi cloud, long? start = null, long? end = null)
            : this(file.Parts, cloud, start, end)
        {
        }

        public DownloadStream(IList<File> files, CloudApi cloud, long? start = null, long? end = null)
        {
            var globalLength = files.Sum(f => f.Size);

            _cloud = cloud;
            _files = files;
            _start = start;
            _end = end >= globalLength ? globalLength - 1 : end;

            Length = _start != null && _end != null
                ? _end.Value - _start.Value + 1
                : globalLength;

            Initialize();
        }

        private void Initialize()
        {
            _innerStream = new RingBufferedStream(InnerBufferSize);

            // ReSharper disable once UnusedVariable
            var t = GetFileStream();
        }

        private Task<WebResponse> _task;

        private async Task<object> GetFileStream()
        {
            var totalLength = Length;
            long glostart = _start ?? 0;
            long gloend = _end == null || (_start == _end && _end == 0) ? totalLength : _end.Value + 1;

            long fileStart = 0;
            long fileEnd = 0;

            _task = Task.FromResult((WebResponse)null);

            foreach (var file in _files)
            {
                var clofile = file;

                fileEnd += clofile.Size;

                if (glostart >= fileEnd || gloend <= fileStart)
                {
                    fileStart += clofile.Size;
                    continue;
                }
                
                long clostart = Math.Max(0, glostart - fileStart);
                long cloend = gloend - fileStart - 1;

                _task = _task.ContinueWith(task1 =>
                {
                    
                    WebResponse response;
                    int retryCnt = 0;
                    while (true)
                    {
                        try
                        {
                            var request = CreateRequest(clostart, cloend, clofile, retryCnt > 0);
                            Logger.Debug($"HTTP:{request.Method}:{request.RequestUri.AbsoluteUri}");

                            response = request.GetResponse();
                            break;
                        }
                        catch (Exception wex)
                        {
                            if (++retryCnt <= 3)
                            {
                                Logger.Warn($"HTTP: Failed with {wex.Message} ");
                                continue;
                            }
                            Logger.Error($"GetFileStream failed with {wex}");
                            _innerStream.Dispose();
                            throw;
                        }
                    }

                    using (Stream responseStream = response.GetResponseStream())
                    {
                        responseStream?.CopyTo(_innerStream);
                    }

                    return response;
                }, TaskContinuationOptions.OnlyOnRanToCompletion);

                fileStart += file.Size;
            }

            _task = _task.ContinueWith(task1 =>
            {
                _innerStream.Flush();
                return (WebResponse)null;
            }, TaskContinuationOptions.OnlyOnRanToCompletion);

            return _innerStream;
        }

        private ShardInfo GetShard(File file)
        {
            var res = string.IsNullOrEmpty(file.PublicLink)
                ? _cloud.Account.GetShardInfo(ShardType.Get).Result
                : _cloud.Account.GetShardInfo(ShardType.WeblinkGet).Result;
            return res;
        }
        private HttpWebRequest CreateRequest(long instart, long inend, File file, bool doBanCurrentShard)
        {
            if (doBanCurrentShard)
                _cloud.Account.BanShardInfo(_shard);

            _shard = GetShard(file);

            string downloadkey = string.Empty;
            if (_shard.Type == ShardType.WeblinkGet)
                downloadkey = _cloud.Account.DownloadToken.Value;

            var request = _shard.Type == ShardType.Get
                ? (HttpWebRequest)WebRequest.Create($"{_shard.Url}{Uri.EscapeDataString(file.FullPath)}")
                : (HttpWebRequest)WebRequest.Create($"{_shard.Url}{new Uri(file.PublicLink).PathAndQuery.Remove(0, "/public".Length)}?key={downloadkey}");

            request.Headers.Add("Accept-Ranges", "bytes");
            request.AddRange(instart, inend);
            request.Proxy = _cloud.Account.Proxy;
            request.CookieContainer = _cloud.Account.Cookies;
            request.Method = "GET";
            request.ContentType = MediaTypeNames.Application.Octet;
            request.Accept = "*/*";
            request.UserAgent = ConstSettings.UserAgent;
            request.AllowReadStreamBuffering = false;

            request.Timeout = 15 * 1000;

            return request;
        }


        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;

            _task.Wait();
            _innerStream.Close();
        }


        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return _innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int readed = _innerStream.Read(buffer, offset, count);
            return readed;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override bool CanRead { get; } = true;
        public override bool CanSeek { get; } = true;
        public override bool CanWrite { get; } = false;

        public override long Length { get; }

        public override long Position { get; set; }
    }
}

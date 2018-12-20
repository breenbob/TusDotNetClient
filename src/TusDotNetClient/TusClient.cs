﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace TusDotNetClient
{
    public partial class TusClient
    {
        public delegate void UploadingEvent(long bytesTransferred, long bytesTotal);

        public event UploadingEvent Uploading;

        public delegate void DownloadingEvent(long bytesTransferred, long bytesTotal);

        public event DownloadingEvent Downloading;

        private CancellationTokenSource cancelSource = new CancellationTokenSource();

        public IWebProxy Proxy { get; set; }

        public void Cancel()
        {
            cancelSource.Cancel();
        }

        public string Create(string url, FileInfo file, Dictionary<string, string> metadata = null)
        {
            if (metadata == null)
            {
                metadata = new Dictionary<string, string>();
            }

            if (!metadata.ContainsKey("filename"))
            {
                metadata["filename"] = file.Name;
            }

            return Create(url, file.Length, metadata);
        }

        public string Create(string url, long uploadLength, Dictionary<string, string> metadata = null)
        {
            var requestUri = new Uri(url);
            var client = new TusHttpClient();
            client.Proxy = Proxy;

            var request = new TusHttpRequest(url, RequestMethod.Post);
            request.AddHeader("Upload-Length", uploadLength.ToString());
            request.AddHeader("Content-Length", "0");

            metadata = metadata ?? new Dictionary<string, string>();

            var metadatastrings = new List<string>();
            foreach (var meta in metadata)
            {
                string k = meta.Key.Replace(" ", "").Replace(",", "");
                string v = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(meta.Value));
                metadatastrings.Add(string.Format("{0} {1}", k, v));
            }

            request.AddHeader("Upload-Metadata", string.Join(",", metadatastrings.ToArray()));

            var response = client.PerformRequest(request);

            if (response.StatusCode == HttpStatusCode.Created)
            {
                if (response.Headers.ContainsKey("Location"))
                {
                    Uri locationUri;
                    if (Uri.TryCreate(response.Headers["Location"], UriKind.RelativeOrAbsolute, out locationUri))
                    {
                        if (!locationUri.IsAbsoluteUri)
                        {
                            locationUri = new Uri(requestUri, locationUri);
                        }

                        return locationUri.ToString();
                    }
                    else
                    {
                        throw new Exception("Invalid Location Header");
                    }
                }
                else
                {
                    throw new Exception("Location Header Missing");
                }
            }
            else
            {
                throw new Exception("CreateFileInServer failed. " + response.ResponseString);
            }
        }

        public void Upload(string url, FileInfo file)
        {
            using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read))
            {
                Upload(url, fs);
            }
        }

        public void Upload(string url, Stream fs)
        {
            var offset = getFileOffset(url);
            var client = new TusHttpClient();
            System.Security.Cryptography.SHA1 sha = new System.Security.Cryptography.SHA1Managed();
            int chunkSize = (int) Math.Ceiling(3 * 1024.0 * 1024.0); //3 mb

            if (offset == fs.Length)
            {
                if (Uploading != null)
                    Uploading(fs.Length, fs.Length);
            }


            while (offset < fs.Length)
            {
                fs.Seek(offset, SeekOrigin.Begin);
                byte[] buffer = new byte[chunkSize];
                var bytesRead = fs.Read(buffer, 0, chunkSize);

                Array.Resize(ref buffer, bytesRead);
                var sha1Hash = sha.ComputeHash(buffer);

                var request = new TusHttpRequest(url, RequestMethod.Patch, buffer, cancelSource.Token);
                request.AddHeader("Upload-Offset", offset.ToString());
                request.AddHeader("Upload-Checksum", "sha1 " + Convert.ToBase64String(sha1Hash));
                request.AddHeader("Content-Type", "application/offset+octet-stream");

                request.Uploading += delegate(long bytesTransferred, long bytesTotal)
                {
                    if (Uploading != null)
                        Uploading(offset + bytesTransferred, fs.Length);
                };

                try
                {
                    var response = client.PerformRequest(request);

                    if (response.StatusCode == HttpStatusCode.NoContent)
                    {
                        offset += bytesRead;
                    }
                    else
                    {
                        throw new Exception("WriteFileInServer failed. " + response.ResponseString);
                    }
                }
                catch (IOException ex)
                {
                    if (ex.InnerException is SocketException socketException)
                    {
                        if (socketException.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            // retry by continuing the while loop but get new offset from server to prevent Conflict error
                            offset = getFileOffset(url);
                        }
                        else
                        {
                            throw socketException;
                        }
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }

        public TusHttpResponse Download(string url)
        {
            var client = new TusHttpClient();

            var request = new TusHttpRequest(url, RequestMethod.Get, null, cancelSource.Token);

            request.Downloading += delegate(long bytesTransferred, long bytesTotal)
            {
                Downloading?.Invoke(bytesTransferred, bytesTotal);
            };

            var response = client.PerformRequest(request);

            return response;
        }

        public TusHttpResponse Head(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Head);

            try
            {
                var response = client.PerformRequest(request);
                return response;
            }
            catch (TusException ex)
            {
                var response = new TusHttpResponse();
                response.StatusCode = ex.StatusCode;
                return response;
            }
        }

        public TusServerInfo GetServerInfo(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Options);

            var response = client.PerformRequest(request);

            if (response.StatusCode != HttpStatusCode.NoContent && response.StatusCode != HttpStatusCode.OK)
                throw new Exception("getServerInfo failed. " + response.ResponseString);

            // Spec says NoContent but tusd gives OK because of browser bugs
            response.Headers.TryGetValue(TusHeaderNames.TusResumable, out var version);
            response.Headers.TryGetValue(TusHeaderNames.TusVersion, out var supportedVersion);
            response.Headers.TryGetValue(TusHeaderNames.TusExtension, out var extensions);
            response.Headers.TryGetValue(TusHeaderNames.TusMaxSize, out var maxSizeString);
                long.TryParse(maxSizeString, out var maxSize);

            return new TusServerInfo(version, supportedVersion, extensions, maxSize);
        }

        public bool Delete(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Delete);

            var response = client.PerformRequest(request);

            if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotFound ||
                response.StatusCode == HttpStatusCode.Gone)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        private long getFileOffset(string url)
        {
            var client = new TusHttpClient();
            var request = new TusHttpRequest(url, RequestMethod.Head);

            var response = client.PerformRequest(request);

            if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK)
            {
                if (response.Headers.ContainsKey("Upload-Offset"))
                {
                    return long.Parse(response.Headers["Upload-Offset"]);
                }
                else
                {
                    throw new Exception("Offset Header Missing");
                }
            }
            else
            {
                throw new Exception("getFileOffset failed. " + response.ResponseString);
            }
        }
    }
}
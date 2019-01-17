﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace Sir.HttpServer.Controllers
{
    [Route("io")]
    public class IOController : Controller
    {
        private readonly PluginsCollection _plugins;

        public IOController(PluginsCollection plugins)
        {
            _plugins = plugins;
        }

        [HttpPost("{*collectionId}")]
        public async Task<IActionResult> Post(string collectionId)
        {
            if (collectionId == null)
            {
                throw new ArgumentNullException(nameof(collectionId));
            }

            var writer = _plugins.Get<IWriter>(Request.ContentType);

            if (writer == null)
            {
                throw new NotSupportedException(); // Media type not supported
            }

            try
            {
                var timer = new Stopwatch();
                timer.Start();

                Result result = await writer.Write(collectionId, Request);
                Logging.Log("write took {0}", timer.Elapsed);
                timer.Restart();

                var buf = result.Data.ToArray();
                Logging.Log("serialized response in {0}", timer.Elapsed);

                return new FileContentResult(buf, result.MediaType);
            }
            catch (Exception ew)
            {
                Logging.Log(ew);
                throw ew;
            }
        }

        [HttpGet("{*collectionId}")]
        [HttpPut("{*collectionId}")]
        public async Task<IActionResult> Get(string collectionId)
        {
            var mediaType = Request.Headers["Accept"].ToArray()[0];
            var reader = _plugins.Get<IReader>(mediaType);

            if (reader == null)
            {
                throw new NotSupportedException(); // Media type not supported
            }

            try
            {
                var timer = new Stopwatch();
                timer.Start();

                var result = await reader.Read(collectionId, Request);

                Logging.Log("processed {0} request in {1}", mediaType, timer.Elapsed);

                if (result.Data == null)
                {
                    return new FileContentResult(new byte[0], result.MediaType);
                }
                else
                {
                    Response.Headers.Add("X-Total", result.Total.ToString());

                    var buf = result.Data.ToArray();

                    Logging.Log("serialized {0} response in {1}", reader.GetType().ToString(), timer.Elapsed);

                    return new FileContentResult(buf, result.MediaType);
                }
            }
            catch (Exception ew)
            {
                Logging.Log(ew);
                throw ew;
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            Logging.Close();
        }
    }
}
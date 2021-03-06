﻿using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using tusdotnet.Helpers;
using tusdotnet.Models.Expiration;
using tusdotnet.Stores;
using tusdotnet;
using tusdotnet.Models;
using tusdotnet.Models.Configuration;
using tusdotnet.Models.Concatenation;

namespace AspNetCore_Net462_TestApp
{
    public class Startup
    {
        private readonly AbsoluteExpiration _absoluteExpiration = new AbsoluteExpiration(TimeSpan.FromMinutes(5));
        private readonly TusDiskStore _tusDiskStore = new TusDiskStore(@"C:\tusfiles\");

        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddCors();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IHostingEnvironment env, ILoggerFactory loggerFactory,
           IApplicationLifetime applicationLifetime)
        {
            loggerFactory.AddConsole();

            var logger = loggerFactory.CreateLogger<Startup>();

            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseDefaultFiles();
            app.UseStaticFiles();

            app.UseCors(builder => builder
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowAnyOrigin()
                .WithExposedHeaders(CorsHelper.GetExposedHeaders()));

            app.Use(async (context, next) =>
            {
                try
                {
                    await next.Invoke();
                }
                catch (Exception exc)
                {
                    logger.LogError(null, exc, exc.Message);
                    context.Response.StatusCode = 500;
                    await context.Response.WriteAsync("An internal server error has occurred", context.RequestAborted);
                }
            });

            app.UseTus(context => new DefaultTusConfiguration
            {
                UrlPath = "/files",
                Store = new TusDiskStore(@"C:\tusfiles\"),
                Events = new Events
                {
                    OnBeforeCreateAsync = ctx =>
                    {
                        // Partial files are not complete so we do not need to validate
                        // the metadata in our example.
                        if (ctx.FileConcatenation is FileConcatPartial)
                        {
                            return Task.CompletedTask;
                        }

                        if (!ctx.Metadata.ContainsKey("name"))
                        {
                            ctx.FailRequest("name metadata must be specified. ");
                        }

                        if (!ctx.Metadata.ContainsKey("contentType"))
                        {
                            ctx.FailRequest("contentType metadata must be specified. ");
                        }

                        return Task.CompletedTask;
                    },
                    OnCreateCompleteAsync = ctx =>
                    {
                        logger.LogInformation($"Created file {ctx.FileId} using {ctx.Store.GetType().FullName}");
                        return Task.CompletedTask;
                    },
                    OnBeforeDeleteAsync = ctx =>
                    {
                        // Can the file be deleted? If not call ctx.FailRequest(<message>);
                        return Task.CompletedTask;
                    },
                    OnDeleteCompleteAsync = ctx =>
                    {
                        logger.LogInformation($"Deleted file {ctx.FileId} using {ctx.Store.GetType().FullName}");
                        return Task.CompletedTask;
                    },
                    OnFileCompleteAsync = ctx =>
                    {
                        logger.LogInformation($"Upload of {ctx.FileId} completed using {ctx.Store.GetType().FullName}");
                        // If the store implements ITusReadableStore one could access the completed file here.
                        // The default TusDiskStore implements this interface:
                        //var file = await ((tusdotnet.Interfaces.ITusReadableStore)ctx.Store).GetFileAsync(ctx.FileId, ctx.CancellationToken);
                        return Task.CompletedTask;
                    }
                },
                // Set an expiration time where incomplete files can no longer be updated.
                // This value can either be absolute or sliding.
                // Absolute expiration will be saved per file on create
                // Sliding expiration will be saved per file on create and updated on each patch/update.
                Expiration = _absoluteExpiration
            });

            app.Use(async (context, next) =>
            {
                // All GET requests to tusdotnet are forwared so that you can handle file downloads.
                // This is done because the file's metadata is domain specific and thus cannot be handled 
                // in a generic way by tusdotnet.
                if (!context.Request.Method.Equals("get", StringComparison.OrdinalIgnoreCase))
                {
                    await next.Invoke();
                    return;
                }

                var url = new Uri(context.Request.GetDisplayUrl());

                if (url.LocalPath.StartsWith("/files/", StringComparison.Ordinal))
                {
                    var fileId = url.LocalPath.Replace("/files/", "").Trim();
                    if (!string.IsNullOrEmpty(fileId))
                    {
                        var file = await _tusDiskStore.GetFileAsync(fileId, context.RequestAborted);

                        if (file == null)
                        {
                            context.Response.StatusCode = 404;
                            await context.Response.WriteAsync($"File with id {fileId} was not found.",
                                context.RequestAborted);
                            return;
                        }

                        var fileStream = await file.GetContentAsync(context.RequestAborted);
                        var metadata = await file.GetMetadataAsync(context.RequestAborted);

                        context.Response.ContentType = metadata.ContainsKey("contentType")
                            ? metadata["contentType"].GetString(Encoding.UTF8)
                            : "application/octet-stream";

                        if (metadata.TryGetValue("name", out var nameMeta))
                        {
                            context.Response.Headers.Add("Content-Disposition",
                                new[] { $"attachment; filename=\"{nameMeta.GetString(Encoding.UTF8)}\"" });
                        }

                        await fileStream.CopyToAsync(context.Response.Body, 81920, context.RequestAborted);
                    }
                }
            });

            // Setup cleanup job to remove incomplete expired files.
            // This is just a simple example. In production one would use a cronjob/webjob and poll an endpoint that runs RemoveExpiredFilesAsync.
            var onAppDisposingToken = applicationLifetime.ApplicationStopping;
            Task.Run(async () =>
            {
                while (!onAppDisposingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Running cleanup job...");
                    var numberOfRemovedFiles = await _tusDiskStore.RemoveExpiredFilesAsync(onAppDisposingToken);
                    logger.LogInformation(
                        $"Removed {numberOfRemovedFiles} expired files. Scheduled to run again in {_absoluteExpiration.Timeout.TotalMilliseconds} ms");
                    await Task.Delay(_absoluteExpiration.Timeout, onAppDisposingToken);
                }
            }, onAppDisposingToken);
        }
    }
}

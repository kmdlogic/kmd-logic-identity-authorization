﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Rest;
using Microsoft.Rest.Serialization;
using Newtonsoft.Json;

namespace Kmd.Logic.Authentication
{
    /// <summary>
    /// Generate authorization tokens required to access Logic services.
    /// </summary>
    /// <remarks>
    /// The LogicTokenProviderFactory is intended to be a long-lived class.
    /// </remarks>
    public sealed class LogicTokenProviderFactory : IDisposable
    {
        private readonly LogicTokenProviderOptions options;
        private readonly SemaphoreSlim semaphore = new SemaphoreSlim(1, 1);
        private readonly JsonSerializerSettings jsonSerializerSettings = new JsonSerializerSettings();

        private DateTime expiration = DateTime.Now;
        private TokenResponse currentToken;

        /// <summary>
        /// Initializes a new instance of the <see cref="LogicTokenProviderFactory"/> class.
        /// </summary>
        /// <param name="options">The required configuration options.</param>
        public LogicTokenProviderFactory(LogicTokenProviderOptions options)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Get a token provider which can issue authorization header.
        /// </summary>
        /// <param name="httpClient">The HTTP client to use. The caller is expected to manage this resource and it will not be disposed.</param>
        /// <returns>A authorization token provider.</returns>
        public ITokenProvider GetProvider(HttpClient httpClient)
        {
            return new TokenProvider(this, httpClient, this.jsonSerializerSettings);
        }

        internal class TokenProvider : ITokenProvider
        {
            private readonly LogicTokenProviderFactory parent;
            private readonly HttpClient httpClient;
            private readonly JsonSerializerSettings jsonSerializerSettings;

            public TokenProvider(LogicTokenProviderFactory parent, HttpClient httpClient, JsonSerializerSettings jsonSerializerSettings)
            {
                this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
                this.parent = parent;
                this.jsonSerializerSettings = jsonSerializerSettings;
            }

            public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
            {
                await this.parent.semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (this.parent.currentToken != null && this.parent.expiration > DateTime.Now)
                    {
                        return new AuthenticationHeaderValue(this.parent.currentToken.TokenType, this.parent.currentToken.AccessToken);
                    }

                    this.parent.currentToken = null;

                    var expire = DateTime.Now;

                    var token = await this.RequestToken(
                        this.httpClient,
                        this.parent.options.AuthorizationTokenIssuer,
                        this.parent.options.ClientId,
                        this.parent.options.AuthorizationScope,
                        this.parent.options.ClientSecret,
                        cancellationToken)
                        .ConfigureAwait(false);

                    this.parent.expiration = expire.AddSeconds(token.ExpiresIn - 5);

                    if (string.IsNullOrEmpty(token.AccessToken))
                    {
                        throw new LogicTokenProviderException("Unable to get a token from the token issuer");
                    }

                    this.parent.currentToken = token;

                    return new AuthenticationHeaderValue(this.parent.currentToken.TokenType, this.parent.currentToken.AccessToken);
                }
                finally
                {
                    this.parent.semaphore.Release();
                }
            }

            private async Task<TokenResponse> RequestToken(HttpClient httpClient, Uri uriAuthorizationServer, string clientId, string scope, string clientSecret, CancellationToken cancellationToken)
            {
                HttpResponseMessage responseMessage;

                var tokenRequest = new HttpRequestMessage(HttpMethod.Post, uriAuthorizationServer);
                tokenRequest.Content = new FormUrlEncodedContent(
                    new[]
                    {
                        new KeyValuePair<string, string>("grant_type", "client_credentials"),
                        new KeyValuePair<string, string>("client_id", clientId),
                        new KeyValuePair<string, string>("scope", scope),
                        new KeyValuePair<string, string>("client_secret", clientSecret),
                    });

                responseMessage = await httpClient.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);

                if (!responseMessage.IsSuccessStatusCode)
                {
                    throw new LogicTokenProviderException("Unable to access the token issuer");
                }

                var json = await responseMessage
                    .Content
                    .ReadAsStringAsync()
                    .ConfigureAwait(false);

                return SafeJsonConvert.DeserializeObject<TokenResponse>(json, this.jsonSerializerSettings);
            }
        }

        public void Dispose()
        {
            this.semaphore.Dispose();
        }

        internal class TokenResponse
        {
            [JsonProperty("token_type")]
            public string TokenType { get; set; }

            [JsonProperty("expires_in")]
            public int ExpiresIn { get; set; }

            [JsonProperty("ext_expires_in")]
            public int ExtExpiresIn { get; set; }

            [JsonProperty("access_token")]
            public string AccessToken { get; set; }
        }
    }
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Wrap;

namespace HotelCaliVegasSearch
{
    public class HotelSearch
    {
        private readonly HttpClient _client;
        private readonly ILogger _log;
        private HotelCaliVegasSearch.Models.HotelInfoList HotelList;

        public HotelSearch(ILogger log, HttpClient client)
        {
            _log = log;
            _client = client;
            string filepath = "LasVegasHotelList.json";
            string readResult = string.Empty;
            string writeResult = string.Empty;
            using (StreamReader hotelsFromJson = new StreamReader(filepath))
            {
                var stringHotelsFromJson = hotelsFromJson.ReadToEnd();
                HotelList.Hotels = JsonConvert.DeserializeObject<HotelCaliVegasSearch.Models.HotelInfo[]>(stringHotelsFromJson).ToList();
            }
        }

        public async Task<List<string>> FindHotels(string city, string state, decimal radius)
        {
            List<string> hotelsFound = new List<string>();
            CultureInfo culture =  new CultureInfo("en-US");

            _log.LogInformation("Searching for hotels");

            foreach( var data in HotelList.Hotels)
            {
                if(data.City == city && data.State == state)
                {
                    //(36.1337103, 115.0850568)
                    string tempLocation = data.Location;
                    // get the right part alone
                    decimal locationY = Convert.ToDecimal(tempLocation.Split(','), culture);
                    if(locationY <= radius)
                    {
                        hotelsFound.Add(data.LocationName);
                    }
                }
            };

            return hotelsFound;

            //return await MakeOCRRequest(imageBytes);
        }

        /// <summary>
        /// Creates a Polly-based resiliency strategy that does the following when communicating
        /// with the external (downstream) Computer Vision API service:
        /// 
        /// If requests to the service are being throttled, as indicated by 429 or 503 responses,
        /// wait and try again in a bit by exponentially backing off each time. This should give the service
        /// enough time to recover or allow enough time to pass that removes the throttling restriction.
        /// This is implemented through the WaitAndRetry policy named 'waitAndRetryPolicy'.
        /// 
        /// Alternately, if requests to the service result in an HttpResponseException, or a number of
        /// status codes worth retrying (such as 500, 502, or 504), break the circuit to block any more
        /// requests for the specified period of time, send a test request to see if the error is still
        /// occurring, then reset the circuit once successful.
        /// 
        /// These policies are executed through a PolicyWrap, which combines these into a resiliency
        /// strategy. For more information, see: https://github.com/App-vNext/Polly/wiki/PolicyWrap
        /// 
        /// NOTE: A longer-term resiliency strategy would have us share the circuit breaker state across
        /// instances, ensuring subsequent calls to the struggling downstream service from new instances
        /// adhere to the circuit state, allowing that service to recover. This could possibly be handled
        /// by a Distributed Circuit Breaker (https://github.com/App-vNext/Polly/issues/287) in the future,
        /// or perhaps by using Durable Functions that can hold the state.
        /// </summary>
        /// <returns></returns>
        private AsyncPolicyWrap<HttpResponseMessage> DefineAndRetrieveResiliencyStrategy()
        {
            // Retry when these status codes are encountered.
            HttpStatusCode[] httpStatusCodesWorthRetrying = {
               HttpStatusCode.InternalServerError, // 500
               HttpStatusCode.BadGateway, // 502
               HttpStatusCode.GatewayTimeout // 504
            };

            // Immediately fail (fail fast) when these status codes are encountered.
            HttpStatusCode[] httpStatusCodesToImmediatelyFail = {
               HttpStatusCode.BadRequest, // 400
               HttpStatusCode.Unauthorized, // 401
               HttpStatusCode.Forbidden // 403
            };

            // Define our waitAndRetry policy: retry n times with an exponential backoff in case the Computer Vision API throttles us for too many requests.
            var waitAndRetryPolicy = Policy
                .Handle<HttpRequestException>()
                .OrResult<HttpResponseMessage>(e => e.StatusCode == HttpStatusCode.ServiceUnavailable ||
                    e.StatusCode == (System.Net.HttpStatusCode)429 || e.StatusCode == (System.Net.HttpStatusCode)403)
                .WaitAndRetryAsync(10, // Retry 10 times with a delay between retries before ultimately giving up
                    attempt => TimeSpan.FromSeconds(0.25 * Math.Pow(2, attempt)), // Back off!  2, 4, 8, 16 etc times 1/4-second
                                                                                  //attempt => TimeSpan.FromSeconds(6), // Wait 6 seconds between retries
                    (exception, calculatedWaitDuration) =>
                    {
                        _log.LogWarning($"Computer Vision API server is throttling our requests. Automatically delaying for {calculatedWaitDuration.TotalMilliseconds}ms");
                    }
                );

            // Define our first CircuitBreaker policy: Break if the action fails 4 times in a row.
            // This is designed to handle Exceptions from the Computer Vision API, as well as
            // a number of recoverable status messages, such as 500, 502, and 504.
            var circuitBreakerPolicyForRecoverable = Policy
                .Handle<HttpResponseException>()
                .OrResult<HttpResponseMessage>(r => httpStatusCodesWorthRetrying.Contains(r.StatusCode))
                .CircuitBreakerAsync(
                    handledEventsAllowedBeforeBreaking: 3,
                    durationOfBreak: TimeSpan.FromSeconds(3),
                    onBreak: (outcome, breakDelay) =>
                    {
                        _log.LogWarning($"Polly Circuit Breaker logging: Breaking the circuit for {breakDelay.TotalMilliseconds}ms due to: {outcome.Exception?.Message ?? outcome.Result.StatusCode.ToString()}");
                    },
                    onReset: () => _log.LogInformation("Polly Circuit Breaker logging: Call ok... closed the circuit again"),
                    onHalfOpen: () => _log.LogInformation("Polly Circuit Breaker logging: Half-open: Next call is a trial")
                );

            // Combine the waitAndRetryPolicy and circuit breaker policy into a PolicyWrap. This defines our resiliency strategy.
            return Policy.WrapAsync(waitAndRetryPolicy, circuitBreakerPolicyForRecoverable);
        }
    }
}

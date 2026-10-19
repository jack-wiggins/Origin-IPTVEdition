using System;
using Newtonsoft.Json.Linq;

namespace Origin.Core.SchedulesDirect
{
    /// <summary>A Schedules Direct error, with a message written for the person holding the remote.</summary>
    public sealed class SdApiException : Exception
    {
        public SdApiException(string message, Exception inner = null) : base(message, inner)
        {
        }

        public SdApiException(string message, int httpStatus, int code = 0, string response = null) : base(message)
        {
            HttpStatus = httpStatus;
            Code = code;
            Response = response;
        }

        public int HttpStatus { get; }

        /// <summary>Schedules Direct's numeric code, e.g. 4003.</summary>
        public int Code { get; }

        /// <summary>Schedules Direct's symbolic code, e.g. "INVALID_USER".</summary>
        public string Response { get; }

        public bool IsTokenProblem =>
            Code == 4006 || Response == "TOKEN_EXPIRED" || Response == "INVALID_TOKEN" || Response == "TOKEN_MISSING";

        public bool IsNoLineups => Code == 4102 || Response == "NO_LINEUPS";

        internal static SdApiException FromResponse(JObject json, int httpStatus)
        {
            var code = (int?)json["code"] ?? 0;
            var response = (string)json["response"];
            var serverMessage = (string)json["message"];
            return new SdApiException(Describe(code, response, serverMessage), httpStatus, code, response);
        }

        private static string Describe(int code, string response, string serverMessage)
        {
            switch (response)
            {
                case "SERVICE_OFFLINE":
                    return "Schedules Direct is temporarily offline. Try again later.";
                case "INVALID_USER":
                case "INVALID_HASH":
                case "INVALID_PASSWORD":
                    return "Schedules Direct did not accept that username and password.";
                case "ACCOUNT_EXPIRED":
                    return "Your Schedules Direct subscription has expired. Renew it at schedulesdirect.org.";
                case "ACCOUNT_LOCKOUT":
                    return "Your Schedules Direct account is locked after too many sign-in attempts. Wait a while and try again.";
                case "ACCOUNT_DISABLED":
                    return "Your Schedules Direct account is disabled. Contact Schedules Direct support.";
                case "TOKEN_EXPIRED":
                case "INVALID_TOKEN":
                    return "Your Schedules Direct session has expired. Sign in again.";
                case "MAX_LINEUP_CHANGES_REACHED":
                    return "You have reached Schedules Direct's daily limit for lineup changes. Try again tomorrow.";
                case "MAX_LINEUPS":
                    return "Your Schedules Direct account already has the most lineups it allows. Remove one first.";
                case "DUPLICATE_LINEUP":
                    return "That lineup is already on your Schedules Direct account.";
                case "NO_LINEUPS":
                    return "There are no lineups on your Schedules Direct account yet.";
                case "LINEUP_NOT_FOUND":
                case "INVALID_LINEUP":
                case "LINEUP_DELETED":
                    return "Schedules Direct could not find that lineup. It may have been retired.";
            }

            if (!string.IsNullOrWhiteSpace(serverMessage))
            {
                return "Schedules Direct: " + serverMessage.Trim().TrimEnd('.') + ".";
            }

            return "Schedules Direct returned error " + code + (response != null ? " (" + response + ")" : string.Empty) + ".";
        }
    }
}

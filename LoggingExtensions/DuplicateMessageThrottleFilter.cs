using log4net.Core;
using log4net.Filter;
using System;

namespace LoggingExtensions
{
    class DuplicateMessageThrottleFilter : FilterSkeleton
    {
        public bool FilterPercentages { get; set; }
        public int PercentageCutoff { get; set; } = 85;

        private string _lastMessage;
        private readonly object _lock = new object();

        public override FilterDecision Decide(LoggingEvent loggingEvent)
        {
            if (loggingEvent == null)
                return FilterDecision.Neutral;

            NormalizeCutoff();

            string newMessage = loggingEvent.MessageObject?.ToString();
            if (string.IsNullOrWhiteSpace(newMessage))
                return FilterDecision.Accept;

            FilterDecision decision = FilterDecision.Accept;

            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(_lastMessage))
                {
                    if (string.Equals(newMessage, _lastMessage, StringComparison.Ordinal))
                    {
                        decision = FilterDecision.Deny;
                    }
                    else if (FilterPercentages &&
                             TryExtractPercentage(newMessage, out decimal percent) &&
                             percent < PercentageCutoff)
                    {
                        decision = FilterDecision.Deny;
                    }
                }

                _lastMessage = newMessage;
            }

            return decision;
        }

        private void NormalizeCutoff()
        {
            if (PercentageCutoff <= 0 || PercentageCutoff > 100)
                PercentageCutoff = 85;
        }

        private static bool TryExtractPercentage(string message, out decimal percentage)
        {
            percentage = 0m;

            int percentIndex = message.LastIndexOf('%');
            if (percentIndex <= 0)
                return false;

            int spaceIndex = message.LastIndexOf(' ', percentIndex);
            if (spaceIndex < 0) spaceIndex = 0;

            int startIndex = spaceIndex;
            int length = percentIndex - startIndex;

            if (length <= 0 || startIndex >= message.Length)
                return false;

            string candidate = message.Substring(startIndex, length).Trim();

            return decimal.TryParse(candidate, out percentage);
        }
    }
}
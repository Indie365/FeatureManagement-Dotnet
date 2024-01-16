﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Microsoft.FeatureManagement.FeatureFilters
{
    static class RecurrenceEvaluator
    {
        //
        // Error Message
        const string OutOfRange = "The value is out of the accepted range.";
        const string UnrecognizableValue = "The value is unrecognizable.";
        const string RequiredParameter = "Value cannot be null or empty.";
        const string NotMatched = "Start date is not a valid first occurrence.";

        const int DayNumberOfWeek = 7;
        const int MinDayNumberOfMonth = 28;
        const int MinDayNumberOfYear = 365;

        /// <summary>
        /// Checks if a provided timestamp is within any recurring time window specified by the Recurrence section in the time window filter settings.
        /// If the time window filter has an invalid recurrence setting, an exception will be thrown.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <returns>True if the time stamp is within any recurring time window, false otherwise.</returns>
        /// </summary>
        public static bool MatchRecurrence(DateTimeOffset time, TimeWindowFilterSettings settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (!TryValidateRecurrenceSettings(settings, out string paramName, out string reason))
            {
                throw new ArgumentException(reason, paramName);
            }

            if (time < settings.Start.Value)
            {
                return false;
            }

            // time is before the first occurrence or time is beyond the end of the recurrence range
            if (!TryGetPreviousOccurrence(time, settings, out DateTimeOffset previousOccurrence))
            {
                return false;
            }

            if (time <= previousOccurrence + (settings.End.Value - settings.Start.Value))
            {
                return true;
            }

            return false;
        }

        private static bool TryValidateRecurrenceSettings(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            if (settings.Recurrence != null)
            {
                return TryValidateRecurrenceRequiredParameter(settings, out paramName, out reason) &&
                    TryValidateRecurrencePattern(settings, out paramName, out reason) &&
                    TryValidateRecurrenceRange(settings, out paramName, out reason);
            }

            paramName = null;

            reason = null;

            return true;
        }

        private static bool TryValidateRecurrenceRequiredParameter(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            Debug.Assert(settings != null);
            Debug.Assert(settings.Recurrence != null);

            if (settings.Start == null)
            {
                paramName = nameof(settings.Start);

                reason = RequiredParameter;

                return false;
            }

            if (settings.End == null)
            {
                paramName = nameof(settings.End);

                reason = RequiredParameter;

                return false;
            }

            Recurrence recurrence = settings.Recurrence;

            if (recurrence.Pattern == null)
            {
                paramName = $"{nameof(settings.Recurrence)}.{nameof(recurrence.Pattern)}";

                reason = RequiredParameter;

                return false;
            }

            if (recurrence.Range == null)
            {
                paramName = $"{nameof(settings.Recurrence)}.{nameof(recurrence.Range)}";

                reason = RequiredParameter;

                return false;
            }

            if (settings.End.Value - settings.Start.Value <= TimeSpan.Zero)
            {
                paramName = nameof(settings.End);

                reason = OutOfRange;

                return false;
            }

            paramName = null;

            reason = null;

            return true;
        }

        private static bool TryValidateRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            Debug.Assert(settings != null);
            Debug.Assert(settings.Recurrence != null);
            Debug.Assert(settings.Recurrence.Pattern != null);

            if (!TryValidateInterval(settings, out paramName, out reason))
            {
                return false;
            }

            switch (settings.Recurrence.Pattern.Type)
            {
                case RecurrencePatternType.Daily:
                    return TryValidateDailyRecurrencePattern(settings, out paramName, out reason);

                case RecurrencePatternType.Weekly:
                    return TryValidateWeeklyRecurrencePattern(settings, out paramName, out reason);

                case RecurrencePatternType.AbsoluteMonthly:
                    return TryValidateAbsoluteMonthlyRecurrencePattern(settings, out paramName, out reason);

                case RecurrencePatternType.RelativeMonthly:
                    return TryValidateRelativeMonthlyRecurrencePattern(settings, out paramName, out reason);

                case RecurrencePatternType.AbsoluteYearly:
                    return TryValidateAbsoluteYearlyRecurrencePattern(settings, out paramName, out reason);

                case RecurrencePatternType.RelativeYearly:
                    return TryValidateRelativeYearlyRecurrencePattern(settings, out paramName, out reason);

                default:
                    paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Pattern)}.{nameof(settings.Recurrence.Pattern.Type)}";

                    reason = UnrecognizableValue;

                    return false;
            }
        }

        private static bool TryValidateDailyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            TimeSpan intervalDuration = TimeSpan.FromDays(settings.Recurrence.Pattern.Interval);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // No required parameter for "Daily" pattern
            // "Start" is always a valid first occurrence for "Daily" pattern

            paramName = null;

            reason = null;

            return true;
        }

        private static bool TryValidateWeeklyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            TimeSpan intervalDuration = TimeSpan.FromDays(pattern.Interval * DayNumberOfWeek);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // Required parameters
            if (!TryValidateDaysOfWeek(settings, out paramName, out reason))
            {
                return false;
            }

            //
            // Check whether "Start" is a valid first occurrence
            DateTimeOffset start = settings.Start.Value;

            DateTime alignedStart = start.DateTime + GetRecurrenceTimeZone(settings) - start.Offset;

            if (!pattern.DaysOfWeek.Any(day =>
                day == alignedStart.DayOfWeek))
            {
                paramName = nameof(settings.Start);

                reason = NotMatched;

                return false;
            }

            //
            // Check whether the time window duration is shorter than the minimum gap between days of week
            if (!IsDurationCompliantWithDaysOfWeek(timeWindowDuration, pattern.Interval, pattern.DaysOfWeek, pattern.FirstDayOfWeek))
            {
                paramName = nameof(settings.End);

                reason = OutOfRange;

                return false;
            }

            return true;
        }

        private static bool TryValidateAbsoluteMonthlyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            TimeSpan intervalDuration = TimeSpan.FromDays(pattern.Interval * MinDayNumberOfMonth);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // Required parameters
            if (!TryValidateDayOfMonth(settings, out paramName, out reason))
            {
                return false;
            }

            //
            // Check whether "Start" is a valid first occurrence
            DateTimeOffset start = settings.Start.Value;

            DateTime alignedStart = start.DateTime + GetRecurrenceTimeZone(settings) - start.Offset;

            if (alignedStart.Day != pattern.DayOfMonth.Value)
            {
                paramName = nameof(settings.Start);

                reason = NotMatched;

                return false;
            }

            return true;
        }

        private static bool TryValidateRelativeMonthlyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            TimeSpan intervalDuration = TimeSpan.FromDays(pattern.Interval * MinDayNumberOfMonth);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // Required parameters
            if (!TryValidateDaysOfWeek(settings, out paramName, out reason))
            {
                return false;
            }

            //
            // Check whether "Start" is a valid first occurrence
            DateTimeOffset start = settings.Start.Value;

            DateTime alignedStart = start.DateTime + GetRecurrenceTimeZone(settings) - start.Offset;

            if (!pattern.DaysOfWeek.Any(day =>
                    NthDayOfWeekInTheMonth(alignedStart, pattern.Index, day) == alignedStart.Date))
            {
                paramName = nameof(settings.Start);

                reason = NotMatched;

                return false;
            }

            return true;
        }

        private static bool TryValidateAbsoluteYearlyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            TimeSpan intervalDuration = TimeSpan.FromDays(pattern.Interval * MinDayNumberOfYear);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // Required parameters
            if (!TryValidateMonth(settings, out paramName, out reason))
            {
                return false;
            }

            if (!TryValidateDayOfMonth(settings, out paramName, out reason))
            {
                return false;
            }

            //
            // Check whether "Start" is a valid first occurrence
            DateTimeOffset start = settings.Start.Value;

            DateTime alignedStart = start.DateTime + GetRecurrenceTimeZone(settings) - start.Offset;

            if (alignedStart.Day != pattern.DayOfMonth.Value || alignedStart.Month != pattern.Month.Value)
            {
                paramName = nameof(settings.Start);

                reason = NotMatched;

                return false;
            }

            return true;
        }

        private static bool TryValidateRelativeYearlyRecurrencePattern(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            TimeSpan intervalDuration = TimeSpan.FromDays(pattern.Interval * MinDayNumberOfYear);

            TimeSpan timeWindowDuration = settings.End.Value - settings.Start.Value;

            //
            // Time window duration must be shorter than how frequently it occurs
            if (timeWindowDuration > intervalDuration)
            {
                paramName = $"{nameof(settings.End)}";

                reason = OutOfRange;

                return false;
            }

            //
            // Required parameters
            if (!TryValidateDaysOfWeek(settings, out paramName, out reason))
            {
                return false;
            }

            if (!TryValidateMonth(settings, out paramName, out reason))
            {
                return false;
            }

            //
            // Check whether "Start" is a valid first occurrence
            DateTimeOffset start = settings.Start.Value;

            DateTime alignedStart = start.DateTime + GetRecurrenceTimeZone(settings) - start.Offset;

            if (alignedStart.Month != pattern.Month.Value ||
                    !pattern.DaysOfWeek.Any(day =>
                        NthDayOfWeekInTheMonth(alignedStart, pattern.Index, day) == alignedStart.Date))
            {
                paramName = nameof(settings.Start);

                reason = NotMatched;

                return false;
            }

            return true;
        }

        private static bool TryValidateRecurrenceRange(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            Debug.Assert(settings != null);
            Debug.Assert(settings.Recurrence != null);
            Debug.Assert(settings.Recurrence.Range != null);

            if (!TryValidateRecurrenceTimeZone(settings, out paramName, out reason))
            {
                return false;
            }

            switch(settings.Recurrence.Range.Type)
            {
                case RecurrenceRangeType.NoEnd:
                    return true;

                case RecurrenceRangeType.EndDate:
                    return TryValidateEndDate(settings, out paramName, out reason);

                case RecurrenceRangeType.Numbered:
                    return TryValidateNumberOfOccurrences(settings, out paramName, out reason);

                default:
                    paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Range)}.{nameof(settings.Recurrence.Range.Type)}";

                    reason = UnrecognizableValue;

                    return false;
            }
        }

        private static bool TryValidateInterval(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Pattern)}.{nameof(settings.Recurrence.Pattern.Interval)}";

            if (settings.Recurrence.Pattern.Interval <= 0)
            {
                reason = OutOfRange;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateDaysOfWeek(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Pattern)}.{nameof(settings.Recurrence.Pattern.DaysOfWeek)}";

            if (settings.Recurrence.Pattern.DaysOfWeek == null || !settings.Recurrence.Pattern.DaysOfWeek.Any())
            {
                reason = RequiredParameter;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateDayOfMonth(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Pattern)}.{nameof(settings.Recurrence.Pattern.DayOfMonth)}";

            if (settings.Recurrence.Pattern.DayOfMonth == null)
            {
                reason = RequiredParameter;

                return false;
            }

            if (settings.Recurrence.Pattern.DayOfMonth < 1 || settings.Recurrence.Pattern.DayOfMonth > 31)
            {
                reason = OutOfRange;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateMonth(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Pattern)}.{nameof(settings.Recurrence.Pattern.Month)}";

            if (settings.Recurrence.Pattern.Month == null)
            {
                reason = RequiredParameter;

                return false;
            }

            if (settings.Recurrence.Pattern.Month < 1 || settings.Recurrence.Pattern.Month > 12)
            {
                reason = OutOfRange;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateEndDate(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Range)}.{nameof(settings.Recurrence.Range.EndDate)}";

            if (settings.Recurrence.Range.EndDate == null)
            {
                reason = RequiredParameter;

                return false;
            }

            if (settings.Start == null)
            {
                paramName = nameof(settings.Start);

                reason = RequiredParameter;

                return false;
            }

            DateTimeOffset start = settings.Start.Value;

            TimeSpan timeZoneOffset = start.Offset;

            if (settings.Recurrence.Range.RecurrenceTimeZone != null)
            {
                TryParseTimeZone(settings.Recurrence.Range.RecurrenceTimeZone, out timeZoneOffset);
            }

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            DateTime endDate = settings.Recurrence.Range.EndDate.Value.DateTime;

            if (endDate.Date < alignedStart.Date)
            {
                reason = OutOfRange;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateNumberOfOccurrences(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Range)}.{nameof(settings.Recurrence.Range.NumberOfOccurrences)}";

            if (settings.Recurrence.Range.NumberOfOccurrences == null)
            {
                reason = RequiredParameter;

                return false;
            }

            if (settings.Recurrence.Range.NumberOfOccurrences < 1)
            {
                reason = OutOfRange;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryValidateRecurrenceTimeZone(TimeWindowFilterSettings settings, out string paramName, out string reason)
        {
            paramName = $"{nameof(settings.Recurrence)}.{nameof(settings.Recurrence.Range)}.{nameof(settings.Recurrence.Range.RecurrenceTimeZone)}";

            if (settings.Recurrence.Range.RecurrenceTimeZone != null && !TryParseTimeZone(settings.Recurrence.Range.RecurrenceTimeZone, out _))
            {
                reason = UnrecognizableValue;

                return false;
            }

            reason = null;

            return true;
        }

        private static bool TryParseTimeZone(string timeZoneStr, out TimeSpan timeZoneOffset)
        {
            timeZoneOffset = TimeSpan.Zero;

            if (timeZoneStr == null)
            {
                return false;
            }

            if (!timeZoneStr.StartsWith("UTC+") && !timeZoneStr.StartsWith("UTC-"))
            {
                return false;
            }

            if (!TimeSpan.TryParseExact(timeZoneStr.Substring(4), @"hh\:mm", null, out timeZoneOffset))
            {
                return false;
            }

            if (timeZoneStr[3] == '-')
            {
                timeZoneOffset = -timeZoneOffset;
            }

            return true;
        }

        /// <summary>
        /// Try to find the closest previous recurrence occurrence before the provided time stamp according to the recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <returns>True if the closest previous occurrence is within the recurrence range, false otherwise.</returns>
        /// </summary>
        private static bool TryGetPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence)
        {
            previousOccurrence = DateTimeOffset.MaxValue;

            DateTimeOffset start = settings.Start.Value;

            if (time < start)
            {
                return false;
            }

            int numberOfOccurrences;

            switch (settings.Recurrence.Pattern.Type)
            {
                case RecurrencePatternType.Daily:
                    FindDailyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                case RecurrencePatternType.Weekly:
                    FindWeeklyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                case RecurrencePatternType.AbsoluteMonthly:
                    FindAbsoluteMonthlyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                case RecurrencePatternType.RelativeMonthly:
                    FindRelativeMonthlyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                case RecurrencePatternType.AbsoluteYearly:
                    FindAbsoluteYearlyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                case RecurrencePatternType.RelativeYearly:
                    FindRelativeYearlyPreviousOccurrence(time, settings, out previousOccurrence, out numberOfOccurrences);

                    break;

                default:
                    return false;
            }

            RecurrenceRange range = settings.Recurrence.Range;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            if (range.Type == RecurrenceRangeType.EndDate)
            {
                DateTime alignedPreviousOccurrence = previousOccurrence.DateTime + timeZoneOffset - previousOccurrence.Offset;

                return alignedPreviousOccurrence.Date <= range.EndDate.Value.Date;
            }

            if (range.Type == RecurrenceRangeType.Numbered)
            {
                return numberOfOccurrences < range.NumberOfOccurrences;
            }

            return true;
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "Daily" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of complete recurrence intervals which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindDailyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeGap = time - start;

            //
            // netstandard2.0 does not support '/' operator for TimeSpan. After we stop supporting netstandard2.0, we can remove .TotalSeconds.
            int numberOfInterval = (int)Math.Floor(timeGap.TotalSeconds / TimeSpan.FromDays(interval).TotalSeconds);

            previousOccurrence = start.AddDays(numberOfInterval * interval);

            numberOfOccurrences = numberOfInterval;
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "Weekly" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of recurring days of week which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindWeeklyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            previousOccurrence = DateTimeOffset.MaxValue;

            numberOfOccurrences = 0;

            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            TimeSpan timeGap = time - start;

            int remainingDaysOfFirstWeek = RemainingDaysOfTheWeek(alignedStart.DayOfWeek, pattern.FirstDayOfWeek);

            TimeSpan remainingTimeOfFirstInterval = TimeSpan.FromDays(remainingDaysOfFirstWeek) - alignedStart.TimeOfDay + TimeSpan.FromDays((interval - 1) * 7);

            if (remainingTimeOfFirstInterval <= timeGap)
            {
                int numberOfInterval = (int)Math.Floor((timeGap - remainingTimeOfFirstInterval).TotalSeconds / TimeSpan.FromDays(interval * 7).TotalSeconds);

                previousOccurrence = start.AddDays(numberOfInterval * interval * 7 + remainingDaysOfFirstWeek + (interval - 1) * 7);

                numberOfOccurrences += numberOfInterval * pattern.DaysOfWeek.Count();

                //
                // Add the occurrences in the first week
                numberOfOccurrences += 1;

                DateTime dateTime = alignedStart.AddDays(1);

                while (dateTime.DayOfWeek != pattern.FirstDayOfWeek)
                {
                    if (pattern.DaysOfWeek.Any(day =>
                        day == dateTime.DayOfWeek))
                    {
                        numberOfOccurrences += 1;
                    }

                    dateTime = dateTime.AddDays(1);
                }
            }
            else // time is still within the first interval
            {
                previousOccurrence = start;
            }

            DateTime alignedPreviousOccurrence = previousOccurrence.DateTime + timeZoneOffset - previousOccurrence.Offset;

            DateTime alignedTime = time.DateTime + timeZoneOffset - time.Offset;

            while (alignedPreviousOccurrence.AddDays(1) <= alignedTime)
            {
                alignedPreviousOccurrence = alignedPreviousOccurrence.AddDays(1);

                if (alignedPreviousOccurrence.DayOfWeek == pattern.FirstDayOfWeek) // Come to the next week
                {
                    break;
                }

                if (pattern.DaysOfWeek.Any(day =>
                    day == alignedPreviousOccurrence.DayOfWeek))
                {
                    previousOccurrence = new DateTimeOffset(alignedPreviousOccurrence, timeZoneOffset);

                    numberOfOccurrences += 1;
                }
            }
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "AbsoluteMonthly" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of complete recurrence intervals which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindAbsoluteMonthlyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            DateTime alignedTime = time.DateTime + timeZoneOffset - time.Offset;

            int monthGap = (alignedTime.Year - alignedStart.Year) * 12 + alignedTime.Month - alignedStart.Month;

            if (alignedTime.TimeOfDay + TimeSpan.FromDays(alignedTime.Day) < alignedStart.TimeOfDay + TimeSpan.FromDays(alignedStart.Day))
            {
                monthGap -= 1;
            }

            int numberOfInterval = monthGap / interval;

            previousOccurrence = start.AddMonths(numberOfInterval * interval);

            numberOfOccurrences = numberOfInterval;
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "RelativeMonthly" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of complete recurrence intervals which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindRelativeMonthlyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            DateTime alignedTime = time.DateTime + timeZoneOffset - time.Offset;

            int monthGap = (alignedTime.Year - alignedStart.Year) * 12 + alignedTime.Month - alignedStart.Month;

            if (!pattern.DaysOfWeek.Any(day =>
                alignedTime >= NthDayOfWeekInTheMonth(alignedTime, pattern.Index, day) + alignedStart.TimeOfDay))
            {
                //
                // E.g. start is 2023.9.1 (the first Friday in 2023.9) and current time is 2023.10.2 (the first Friday in next month is 2023.10.6)
                // Not a complete monthly interval
                monthGap -= 1;
            }

            int numberOfInterval = monthGap / interval;

            DateTime alignedPreviousOccurrenceMonth = alignedStart.AddMonths(numberOfInterval * interval);

            DateTime alignedPreviousOccurrence = DateTime.MaxValue;

            //
            // Find the first occurence date matched the pattern
            // Only one day of week in the month will be matched
            foreach (DayOfWeek day in pattern.DaysOfWeek)
            {
                DateTime occurrenceDate = NthDayOfWeekInTheMonth(alignedPreviousOccurrenceMonth, pattern.Index, day);

                if (occurrenceDate + alignedStart.TimeOfDay < alignedPreviousOccurrence)
                {
                    alignedPreviousOccurrence = occurrenceDate + alignedStart.TimeOfDay;
                }
            }

            previousOccurrence = new DateTimeOffset(alignedPreviousOccurrence, timeZoneOffset);

            numberOfOccurrences = numberOfInterval;
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "AbsoluteYearly" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of complete recurrence intervals which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindAbsoluteYearlyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            DateTime alignedTime = time.DateTime + timeZoneOffset - time.Offset;

            int yearGap = alignedTime.Year - alignedStart.Year;

            if (alignedTime.TimeOfDay + TimeSpan.FromDays(alignedTime.DayOfYear) < alignedStart.TimeOfDay + TimeSpan.FromDays(alignedStart.DayOfYear))
            {
                yearGap -= 1;
            }

            int numberOfInterval = yearGap / interval;

            previousOccurrence = start.AddYears(numberOfInterval * interval);

            numberOfOccurrences = numberOfInterval;
        }

        /// <summary>
        /// Find the closest previous recurrence occurrence before the provided time stamp according to the "RelativeYearly" recurrence pattern.
        /// <param name="time">A time stamp.</param>
        /// <param name="settings">The settings of time window filter.</param>
        /// <param name="previousOccurrence">The closest previous occurrence.</param>
        /// <param name="numberOfOccurrences">The number of complete recurrence intervals which have occurred between the time and the recurrence start.</param>
        /// </summary>
        private static void FindRelativeYearlyPreviousOccurrence(DateTimeOffset time, TimeWindowFilterSettings settings, out DateTimeOffset previousOccurrence, out int numberOfOccurrences)
        {
            RecurrencePattern pattern = settings.Recurrence.Pattern;

            DateTimeOffset start = settings.Start.Value;

            int interval = pattern.Interval;

            TimeSpan timeZoneOffset = GetRecurrenceTimeZone(settings);

            DateTime alignedStart = start.DateTime + timeZoneOffset - start.Offset;

            DateTime alignedTime = time.DateTime + timeZoneOffset - time.Offset;

            int yearGap = alignedTime.Year - alignedStart.Year;

            if (alignedTime.Month < alignedStart.Month)
            {
                //
                // E.g. start: 2023.9 and time: 2024.8
                // Not a complete yearly interval
                yearGap -= 1;
            }
            else if (alignedTime.Month == alignedStart.Month && !pattern.DaysOfWeek.Any(day =>
                alignedTime >= NthDayOfWeekInTheMonth(alignedTime, pattern.Index, day) + alignedStart.TimeOfDay))
            {
                //
                // E.g. start: 2023.9.1 (the first Friday in 2023.9) and time: 2024.9.2 (the first Friday in 2023.9 is 2024.9.6)
                // Not a complete yearly interval
                yearGap -= 1;
            }

            int numberOfInterval = yearGap / interval;

            DateTime alignedPreviousOccurrenceMonth = alignedStart.AddYears(numberOfInterval * interval);

            DateTime alignedPreviousOccurrence = DateTime.MaxValue;

            //
            // Find the first occurence date matched the pattern
            // Only one day of week in the month will be matched
            foreach (DayOfWeek day in pattern.DaysOfWeek)
            {
                DateTime occurrenceDate = NthDayOfWeekInTheMonth(alignedPreviousOccurrenceMonth, pattern.Index, day);

                if (occurrenceDate + alignedStart.TimeOfDay < alignedPreviousOccurrence)
                {
                    alignedPreviousOccurrence = occurrenceDate + alignedStart.TimeOfDay;
                }
            }

            previousOccurrence = new DateTimeOffset(alignedPreviousOccurrence, timeZoneOffset);

            numberOfOccurrences = numberOfInterval;
        }

        private static TimeSpan GetRecurrenceTimeZone(TimeWindowFilterSettings settings)
        {
            if (!TryParseTimeZone(settings.Recurrence.Range.RecurrenceTimeZone, out TimeSpan timeZoneOffset))
            {
                timeZoneOffset = settings.Start.Value.Offset;
            }

            return timeZoneOffset;
        }

        /// <summary>
        /// Check whether the duration is shorter than the minimum gap between recurrence of days of week.
        /// </summary>
        /// <param name="duration">The time span of the duration.</param>
        /// <param name="interval">The recurrence interval.</param>
        /// <param name="daysOfWeek">The days of the week when the recurrence will occur.</param>
        /// <param name="firstDayOfWeek">The first day of the week.</param>
        /// <returns>True if the duration is compliant with days of week, false otherwise.</returns>
        private static bool IsDurationCompliantWithDaysOfWeek(TimeSpan duration, int interval, IEnumerable<DayOfWeek> daysOfWeek, DayOfWeek firstDayOfWeek)
        {
            if (daysOfWeek.Count() == 1)
            {
                return true;
            }

            DateTime date = DateTime.Today;

            int offset = RemainingDaysOfTheWeek(date.DayOfWeek, firstDayOfWeek);

            // Shift to the first day of the week
            date = date.AddDays(offset);

            DateTime prevOccurrence = DateTime.MinValue;

            TimeSpan minGap = TimeSpan.MaxValue;

            for (int i = 0; i < DayNumberOfWeek; i++)
            {
                if (daysOfWeek.Any(day =>
                    day == date.DayOfWeek))
                {
                    if (prevOccurrence == DateTime.MinValue)
                    {
                        prevOccurrence = date;
                    }
                    else
                    {
                        TimeSpan gap = date - prevOccurrence;

                        if (gap < minGap)
                        {
                            minGap = gap;
                        }

                        prevOccurrence = date;
                    }
                }

                date = date.AddDays(1);
            }

            //
            // It may across weeks. Check the adjacent week if the interval is one week.
            if (interval == 1)
            {
                for (int i = 1; i <= DayNumberOfWeek; i++)
                {
                    //
                    // If there are multiple day in DaysOfWeek, it will eventually enter the following if branch
                    if (daysOfWeek.Any(day =>
                        day == date.DayOfWeek))
                    {
                        TimeSpan gap = date - prevOccurrence;

                        if (gap < minGap)
                        {
                            minGap = gap;
                        }

                        break;
                    }

                    date = date.AddDays(1);
                }
            }

            return minGap >= duration;
        }

        private static int RemainingDaysOfTheWeek(DayOfWeek dayOfWeek, DayOfWeek firstDayOfWeek)
        {
            int remainingDays = (int) dayOfWeek - (int) firstDayOfWeek;

            if (remainingDays < 0)
            {
                return -remainingDays;
            }
            else
            {
                return DayNumberOfWeek - remainingDays;
            }
        }

        /// <summary>
        /// Find the nth day of week in the month of the date time.
        /// </summary>
        /// <param name="dateTime">A date time.</param>
        /// <param name="index">The index of the day of week in the month.</param>
        /// <param name="dayOfWeek">The day of week.</param>
        /// <returns>The data time of the nth day of week in the month.</returns>
        private static DateTime NthDayOfWeekInTheMonth(DateTime dateTime, WeekIndex index, DayOfWeek dayOfWeek)
        {
            var date = new DateTime(dateTime.Year, dateTime.Month, 1);

            //
            // Find the first provided day of week in the month
            while (date.DayOfWeek != dayOfWeek)
            {
                date = date.AddDays(1);
            }

            if (date.AddDays(DayNumberOfWeek * (int) index).Month == dateTime.Month)
            {
                date = date.AddDays(DayNumberOfWeek * (int) index);
            }
            else // There is no the 5th day of week in the month
            {
                //
                // Add 3 weeks to reach the fourth day of week in the month
                date = date.AddDays(DayNumberOfWeek * 3);
            }

            return date;
        }
    }
}
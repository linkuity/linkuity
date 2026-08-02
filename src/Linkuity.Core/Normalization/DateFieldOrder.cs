namespace Linkuity.Core.Normalization;

/// <summary>
/// How to read a slash-separated date whose first two components could each be the day or the
/// month. <c>03/04/1980</c> is 4 March in most of the world and 3 April in the United States,
/// and nothing in the value itself says which.
///
/// This is not a preference. Reading a feed under the wrong order does not fail — dates where
/// the day exceeds twelve fail to parse and pass through unchanged, while every date in the
/// first twelve days of a month parses to a confidently wrong value. The result is a field that
/// is right about two-thirds of the time, with no error to indicate which third is wrong.
///
/// ISO-style dates (yyyy-MM-dd, yyyy/MM/dd) are unambiguous and read the same either way.
/// </summary>
public enum DateFieldOrder
{
    /// <summary>
    /// Month before day: <c>03/04/1980</c> is 3 April. United States convention, and what this
    /// previously hardcoded — the default, so existing behaviour is unchanged.
    /// </summary>
    MonthFirst = 0,

    /// <summary>
    /// Day before month: <c>03/04/1980</c> is 4 March. Convention across most of Europe,
    /// Australia, and much of Asia, Africa and South America.
    /// </summary>
    DayFirst = 1
}

using System;
using System.Data;
using FluentAssertions;
using RecoTool.Helpers;
using Xunit;

namespace RecoTool.Tests.Helpers
{
    /// <summary>
    /// Tests unitaires pour <see cref="DataConversionHelper"/> — couvre toutes les
    /// surcharges Safe* (int / nullable int / decimal / bool / nullable bool /
    /// DateTime / string / double) ainsi que les variantes DataRow.
    /// </summary>
    public class DataConversionHelperTests
    {
        // ---------- Helpers locaux ----------

        private static DataRow MakeRow(string columnName, object value)
        {
            var dt = new DataTable();
            dt.Columns.Add(columnName, typeof(object));
            var r = dt.NewRow();
            r[columnName] = value ?? DBNull.Value;
            dt.Rows.Add(r);
            return r;
        }

        // ===================== SafeGetInt =====================

        [Fact]
        public void SafeGetInt_NullObject_ReturnsZero()
        {
            DataConversionHelper.SafeGetInt(null).Should().Be(0);
        }

        [Fact]
        public void SafeGetInt_DBNull_ReturnsZero()
        {
            DataConversionHelper.SafeGetInt(DBNull.Value).Should().Be(0);
        }

        [Fact]
        public void SafeGetInt_BoxedInt_ReturnsValue()
        {
            DataConversionHelper.SafeGetInt(42).Should().Be(42);
        }

        [Theory]
        [InlineData("123", 123)]
        [InlineData("-7", -7)]
        [InlineData("0", 0)]
        public void SafeGetInt_NumericString_Parses(string input, int expected)
        {
            DataConversionHelper.SafeGetInt(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("3.7", 4)]   // Math.Round → 4
        [InlineData("3.4", 3)]
        [InlineData("-2.5", -2)] // banker's rounding (round-half-to-even)
        public void SafeGetInt_NumericStringWithDecimals_RoundsViaMathRound(string input, int expected)
        {
            DataConversionHelper.SafeGetInt(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("notANumber")]
        [InlineData("")]
        public void SafeGetInt_InvalidString_ReturnsZero(string input)
        {
            DataConversionHelper.SafeGetInt(input).Should().Be(0);
        }

        [Fact]
        public void SafeGetInt_FromDataRow_NullRow_ReturnsZero()
        {
            DataConversionHelper.SafeGetInt(null, "col").Should().Be(0);
        }

        [Fact]
        public void SafeGetInt_FromDataRow_MissingColumn_ReturnsZero()
        {
            var row = MakeRow("present", 5);
            DataConversionHelper.SafeGetInt(row, "missing").Should().Be(0);
        }

        [Fact]
        public void SafeGetInt_FromDataRow_PresentValue_Parses()
        {
            var row = MakeRow("n", "99");
            DataConversionHelper.SafeGetInt(row, "n").Should().Be(99);
        }

        // ===================== SafeGetNullableInt =====================

        [Fact]
        public void SafeGetNullableInt_Null_ReturnsNull()
        {
            DataConversionHelper.SafeGetNullableInt(null).Should().BeNull();
            DataConversionHelper.SafeGetNullableInt(DBNull.Value).Should().BeNull();
        }

        [Fact]
        public void SafeGetNullableInt_BoxedInt_ReturnsValue()
        {
            DataConversionHelper.SafeGetNullableInt(7).Should().Be(7);
        }

        [Fact]
        public void SafeGetNullableInt_ValidString_Parses()
        {
            DataConversionHelper.SafeGetNullableInt("42").Should().Be(42);
        }

        [Fact]
        public void SafeGetNullableInt_InvalidString_ReturnsNull()
        {
            DataConversionHelper.SafeGetNullableInt("abc").Should().BeNull();
        }

        [Fact]
        public void SafeGetNullableInt_FromDataRow_MissingColumn_ReturnsNull()
        {
            var row = MakeRow("col", "5");
            DataConversionHelper.SafeGetNullableInt(row, "other").Should().BeNull();
        }

        // ===================== SafeGetDecimal =====================

        [Fact]
        public void SafeGetDecimal_NullOrDBNull_ReturnsZero()
        {
            DataConversionHelper.SafeGetDecimal(null).Should().Be(0m);
            DataConversionHelper.SafeGetDecimal(DBNull.Value).Should().Be(0m);
        }

        [Fact]
        public void SafeGetDecimal_BoxedDecimal_ReturnsValue()
        {
            DataConversionHelper.SafeGetDecimal(123.45m).Should().Be(123.45m);
        }

        [Theory]
        [InlineData("1234.56", 1234.56)]
        [InlineData("-0.001", -0.001)]
        [InlineData("0", 0.0)]
        public void SafeGetDecimal_StringInvariantCulture_Parses(string input, double expected)
        {
            DataConversionHelper.SafeGetDecimal(input).Should().Be((decimal)expected);
        }

        [Fact]
        public void SafeGetDecimal_StringWithCommaThousand_ParsesAsAny()
        {
            // NumberStyles.Any allows thousand separators
            DataConversionHelper.SafeGetDecimal("1,234.56").Should().Be(1234.56m);
        }

        [Fact]
        public void SafeGetDecimal_InvalidString_ReturnsZero()
        {
            DataConversionHelper.SafeGetDecimal("xyz").Should().Be(0m);
        }

        [Fact]
        public void SafeGetDecimal_FromDataRow_HappyPath()
        {
            var row = MakeRow("amt", 12.5m);
            DataConversionHelper.SafeGetDecimal(row, "amt").Should().Be(12.5m);
        }

        // ===================== SafeGetBool =====================

        [Theory]
        [InlineData(null, false)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("False", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        [InlineData("yes", true)]
        [InlineData("y", true)]
        [InlineData("no", false)]
        [InlineData("garbage", false)]
        public void SafeGetBool_VariousInputs(string input, bool expected)
        {
            object obj = input ?? (object)DBNull.Value;
            DataConversionHelper.SafeGetBool(obj).Should().Be(expected);
        }

        [Fact]
        public void SafeGetBool_BoxedBool_ReturnsValue()
        {
            DataConversionHelper.SafeGetBool(true).Should().BeTrue();
            DataConversionHelper.SafeGetBool(false).Should().BeFalse();
        }

        [Fact]
        public void SafeGetBool_BoxedInt_NonZeroIsTrue()
        {
            DataConversionHelper.SafeGetBool(2).Should().BeTrue();
            DataConversionHelper.SafeGetBool(0).Should().BeFalse();
        }

        // ===================== SafeGetNullableBool =====================

        [Fact]
        public void SafeGetNullableBool_NullOrDBNull_ReturnsNull()
        {
            DataConversionHelper.SafeGetNullableBool(null).Should().BeNull();
            DataConversionHelper.SafeGetNullableBool(DBNull.Value).Should().BeNull();
        }

        [Fact]
        public void SafeGetNullableBool_NonParseableString_ReturnsNull()
        {
            DataConversionHelper.SafeGetNullableBool("maybe").Should().BeNull();
        }

        [Theory]
        [InlineData("true", true)]
        [InlineData("false", false)]
        [InlineData("1", true)]
        [InlineData("0", false)]
        public void SafeGetNullableBool_ValidStrings_Parse(string input, bool expected)
        {
            DataConversionHelper.SafeGetNullableBool(input).Should().Be(expected);
        }

        // ===================== SafeGetDateTime =====================

        [Fact]
        public void SafeGetDateTime_NullOrDBNull_ReturnsNull()
        {
            DataConversionHelper.SafeGetDateTime(null).Should().BeNull();
            DataConversionHelper.SafeGetDateTime(DBNull.Value).Should().BeNull();
        }

        [Fact]
        public void SafeGetDateTime_BoxedDate_ReturnsValue()
        {
            var d = new DateTime(2024, 5, 1, 10, 0, 0);
            DataConversionHelper.SafeGetDateTime(d).Should().Be(d);
        }

        [Fact]
        public void SafeGetDateTime_ParsableString_ReturnsParsedDate()
        {
            var iso = "2024-05-01";
            DataConversionHelper.SafeGetDateTime(iso).Should().Be(new DateTime(2024, 5, 1));
        }

        [Fact]
        public void SafeGetDateTime_UnparsableString_ReturnsNull()
        {
            DataConversionHelper.SafeGetDateTime("not-a-date").Should().BeNull();
        }

        // ===================== SafeGetString =====================

        [Fact]
        public void SafeGetString_NullOrDBNull_ReturnsNull()
        {
            DataConversionHelper.SafeGetString(null).Should().BeNull();
            DataConversionHelper.SafeGetString(DBNull.Value).Should().BeNull();
        }

        [Fact]
        public void SafeGetString_NonNull_ReturnsToString()
        {
            DataConversionHelper.SafeGetString(123).Should().Be("123");
            DataConversionHelper.SafeGetString("abc").Should().Be("abc");
        }

        [Fact]
        public void SafeGetString_FromDataRow_MissingColumn_ReturnsNull()
        {
            var row = MakeRow("col", "value");
            DataConversionHelper.SafeGetString(row, "absent").Should().BeNull();
        }

        // ===================== SafeGetDouble =====================

        [Fact]
        public void SafeGetDouble_NullOrDBNull_ReturnsZero()
        {
            DataConversionHelper.SafeGetDouble(null).Should().Be(0.0);
            DataConversionHelper.SafeGetDouble(DBNull.Value).Should().Be(0.0);
        }

        [Fact]
        public void SafeGetDouble_BoxedDouble_ReturnsValue()
        {
            DataConversionHelper.SafeGetDouble(3.14).Should().Be(3.14);
        }

        [Fact]
        public void SafeGetDouble_StringWithInvariantCulture_Parses()
        {
            DataConversionHelper.SafeGetDouble("2.71").Should().Be(2.71);
        }

        [Fact]
        public void SafeGetDouble_InvalidString_ReturnsZero()
        {
            DataConversionHelper.SafeGetDouble("nope").Should().Be(0.0);
        }
    }
}

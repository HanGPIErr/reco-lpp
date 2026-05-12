using System.Collections.Generic;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests unitaires pour <see cref="TransformationService"/>. Service pur,
    /// sans dépendance DB — testable directement.
    /// </summary>
    public class TransformationServiceTests
    {
        private static TransformationService MakeService(params Country[] countries)
            => new TransformationService(countries ?? new Country[0]);

        // ===== ApplyTransformation routing =====

        [Fact]
        public void ApplyTransformation_NullTransform_ReturnsSourceValue()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "RawLabel", "hello" } };
            sut.ApplyTransformation(data, null).Should().Be("");
        }

        [Fact]
        public void ApplyTransformation_UnknownFunction_ReturnsSourceValue()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "RawLabel", "hello" } };
            var t = new AmbreTransform { AMB_Source = "RawLabel", AMB_TransformationFunction = "DOES_NOT_EXIST" };
            sut.ApplyTransformation(data, t).Should().Be("hello");
        }

        [Fact]
        public void ApplyTransformation_RoutesToGetMbawIDFromLabel()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "RawLabel", "ref MBAW1234ABCD end" } };
            var t = new AmbreTransform { AMB_Source = "RawLabel", AMB_TransformationFunction = "GETMBAWIDFROMLABEL" };
            sut.ApplyTransformation(data, t).Should().Be("MBAW1234ABCD");
        }

        // ===== GetSourceValue (via direct method calls + ApplyTransformation) =====

        [Fact]
        public void ApplyTransformation_BackCompat_LabelMapsToRawLabel()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "RawLabel", "raw value" } };
            var t = new AmbreTransform { AMB_Source = "Label", AMB_TransformationFunction = null };
            sut.ApplyTransformation(data, t).Should().Be("raw value");
        }

        [Fact]
        public void ApplyTransformation_BracketExpression_ResolvesFields()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object>
            {
                { "Account_ID", "FR" },
                { "Event_Num", "EV1" }
            };
            var t = new AmbreTransform { AMB_Source = "[Account_ID]-[Event_Num]" };
            sut.ApplyTransformation(data, t).Should().Be("FR-EV1");
        }

        // ===== GetBookingNameFromID =====

        [Fact]
        public void GetBookingNameFromID_FoundCountry_ReturnsCntId()
        {
            var sut = MakeService(new Country { CNT_Id = "FR", CNT_AmbrePivotCountryId = 250 });
            sut.GetBookingNameFromID("250").Should().Be("FR");
        }

        [Fact]
        public void GetBookingNameFromID_NotFound_ReturnsInputUnchanged()
        {
            var sut = MakeService(new Country { CNT_Id = "FR", CNT_AmbrePivotCountryId = 250 });
            sut.GetBookingNameFromID("999").Should().Be("999");
        }

        [Fact]
        public void GetBookingNameFromID_NullOrEmpty_ReturnsEmpty()
        {
            var sut = MakeService();
            sut.GetBookingNameFromID(null).Should().Be(string.Empty);
            sut.GetBookingNameFromID("").Should().Be(string.Empty);
        }

        // ===== GetMbawIDFromLabel =====

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("no mbaw here", "")]
        [InlineData("ref MBAW1234 end", "MBAW1234")]
        [InlineData("mbaw1234abcd", "MBAW1234ABCD")]
        public void GetMbawIDFromLabel_VariousInputs(string input, string expected)
        {
            MakeService().GetMbawIDFromLabel(input).Should().Be(expected);
        }

        // ===== GetCodesFromLabel =====

        [Fact]
        public void GetCodesFromLabel_LongString_ReturnsLast13Chars()
        {
            // Input length = 23, last 13 chars = "ABCDEF1234567"
            MakeService().GetCodesFromLabel("0123456789ABCDEF1234567")
                .Should().Be("ABCDEF1234567");
        }

        [Fact]
        public void GetCodesFromLabel_ShortString_ReturnsTrimmed()
        {
            MakeService().GetCodesFromLabel("  short  ").Should().Be("short");
        }

        [Fact]
        public void GetCodesFromLabel_NullOrEmpty_ReturnsEmpty()
        {
            MakeService().GetCodesFromLabel(null).Should().Be(string.Empty);
            MakeService().GetCodesFromLabel("").Should().Be(string.Empty);
        }

        // ===== GetTRNFromLabel =====

        [Fact]
        public void GetTRNFromLabel_LongLabel_TakesCharacters43To52()
        {
            // 50 'A's puis "TRN1234567" (10 chars, ce qu'on attend)
            var label = new string('A', 42) + "TRN1234567" + "TAIL";
            MakeService().GetTRNFromLabel(label).Should().Be("TRN1234567");
        }

        [Fact]
        public void GetTRNFromLabel_ShortLabel_ReturnsEmpty()
        {
            MakeService().GetTRNFromLabel("short").Should().Be(string.Empty);
            MakeService().GetTRNFromLabel(null).Should().Be(string.Empty);
        }

        // ===== ExtractForReceivable =====

        [Fact]
        public void ExtractForReceivable_BgiToken_ReturnsUppercased()
        {
            MakeService().ExtractForReceivable("ref bgi202310abcdef1 here")
                .Should().Be("BGI202310ABCDEF1");
        }

        [Fact]
        public void ExtractForReceivable_NoBgi_FallsBackToGuaranteeId()
        {
            MakeService().ExtractForReceivable("ref G1234FR123456789 here")
                .Should().Be("G1234FR123456789");
        }

        [Fact]
        public void ExtractForReceivable_NoTokens_ReturnsEmpty()
        {
            MakeService().ExtractForReceivable("nothing relevant").Should().Be(string.Empty);
            MakeService().ExtractForReceivable(null).Should().Be(string.Empty);
        }

        // ===== RemoveZerosFromStart =====

        [Theory]
        [InlineData("000123", "123")]
        [InlineData("0", "")]
        [InlineData("00", "")]
        [InlineData("123", "123")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void RemoveZerosFromStart_VariousInputs(string input, string expected)
        {
            MakeService().RemoveZerosFromStart(input).Should().Be(expected);
        }

        // ===== AddSign =====

        [Fact]
        public void AddSign_DebitFlag_PrependsMinus()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "", "D" } };
            sut.AddSign("100", data).Should().Be("-100");
        }

        [Fact]
        public void AddSign_DebitFlag_AlreadyNegative_ReturnsAsIs()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "", "D" } };
            sut.AddSign("-100", data).Should().Be("-100");
        }

        [Fact]
        public void AddSign_DebitFlag_StripsLeadingPlus()
        {
            var sut = MakeService();
            var data = new Dictionary<string, object> { { "", "D" } };
            sut.AddSign("+100", data).Should().Be("-100");
        }

        [Fact]
        public void AddSign_CreditOrAbsentFlag_LeavesValueUnchanged()
        {
            var sut = MakeService();
            sut.AddSign("100", new Dictionary<string, object> { { "", "C" } }).Should().Be("100");
            sut.AddSign("100", new Dictionary<string, object>()).Should().Be("100");
            sut.AddSign("100", null).Should().Be("100");
        }

        // ===== DetermineTransactionType =====

        [Fact]
        public void DetermineTransactionType_PivotLabelStartsWithBgi_ReturnsTrigger()
        {
            MakeService().DetermineTransactionType("BGI20240101ABCDEF1", isPivot: true)
                .Should().Be(TransactionType.TRIGGER);
        }

        [Fact]
        public void DetermineTransactionType_ReceivableLabel_ReturnsNull()
        {
            MakeService().DetermineTransactionType("anything", isPivot: false)
                .Should().BeNull();
        }

        [Fact]
        public void DetermineTransactionType_TextSaysToCategorize_ReturnsToCategorize()
        {
            MakeService().DetermineTransactionType("please TO CATEGORIZE this", isPivot: true)
                .Should().Be(TransactionType.TO_CATEGORIZE);
        }

        [Fact]
        public void DetermineTransactionType_PivotWithCategory_UsesEnumDirectly()
        {
            // category encode directement un TransactionType
            MakeService().DetermineTransactionType("any label", isPivot: true, category: (int)TransactionType.PAYMENT)
                .Should().Be(TransactionType.PAYMENT);
        }

        [Fact]
        public void DetermineTransactionType_PivotFallbackParse_DetectsKeywords()
        {
            var sut = MakeService();
            sut.DetermineTransactionType("automatic refund", isPivot: true).Should().Be(TransactionType.PAYMENT);
            sut.DetermineTransactionType("collection here", isPivot: true).Should().Be(TransactionType.COLLECTION);
            sut.DetermineTransactionType("ADJUSTMENT", isPivot: true).Should().Be(TransactionType.ADJUSTMENT);
        }
    }
}

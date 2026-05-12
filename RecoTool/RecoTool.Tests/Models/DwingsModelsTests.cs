using FluentAssertions;
using RecoTool.Models;
using Xunit;

namespace RecoTool.Tests.Models
{
    /// <summary>
    /// Tests pour <see cref="DWINGSInvoice"/> et <see cref="DWINGSGuarantee"/>.
    /// </summary>
    public class DwingsModelsTests
    {
        // ===== DWINGSInvoice.SearchGuaranteeId =====

        [Fact]
        public void SearchGuaranteeId_TrueWhenSenderReferenceMatches()
        {
            var inv = new DWINGSInvoice { SENDER_REFERENCE = "G123" };
            inv.SearchGuaranteeId("G123").Should().BeTrue();
        }

        [Fact]
        public void SearchGuaranteeId_TrueWhenReceiverReferenceMatches()
        {
            var inv = new DWINGSInvoice { RECEIVER_REFERENCE = "G456" };
            inv.SearchGuaranteeId("G456").Should().BeTrue();
        }

        [Fact]
        public void SearchGuaranteeId_TrueWhenBusinessCaseReferenceMatches()
        {
            var inv = new DWINGSInvoice { BUSINESS_CASE_REFERENCE = "G789" };
            inv.SearchGuaranteeId("G789").Should().BeTrue();
        }

        [Fact]
        public void SearchGuaranteeId_FalseWhenNoMatch()
        {
            var inv = new DWINGSInvoice
            {
                SENDER_REFERENCE = "X",
                RECEIVER_REFERENCE = "Y",
                BUSINESS_CASE_REFERENCE = "Z"
            };
            inv.SearchGuaranteeId("DIFFERENT").Should().BeFalse();
        }

        [Fact]
        public void SearchGuaranteeId_CaseSensitive_NoMatchOnDifferentCase()
        {
            // L'implémentation utilise == sur strings → sensible à la casse
            var inv = new DWINGSInvoice { SENDER_REFERENCE = "G123" };
            inv.SearchGuaranteeId("g123").Should().BeFalse();
        }

        // ===== Smoke property tests =====

        [Fact]
        public void DWINGSGuarantee_PropertiesAreReadWrite()
        {
            var g = new DWINGSGuarantee
            {
                GUARANTEE_ID = "G1",
                GUARANTEE_STATUS = "ACTIVE",
                GUARANTEE_TYPE = "ISSU",
                OUTSTANDING_AMOUNT = 1234.5
            };
            g.GUARANTEE_ID.Should().Be("G1");
            g.GUARANTEE_STATUS.Should().Be("ACTIVE");
            g.GUARANTEE_TYPE.Should().Be("ISSU");
            g.OUTSTANDING_AMOUNT.Should().Be(1234.5);
        }
    }
}

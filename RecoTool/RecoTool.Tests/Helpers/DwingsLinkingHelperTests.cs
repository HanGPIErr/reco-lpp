using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using RecoTool.Helpers;
using RecoTool.Services.DTOs;
using Xunit;

namespace RecoTool.Tests.Helpers
{
    /// <summary>
    /// Tests unitaires pour <see cref="DwingsLinkingHelper"/> et <see cref="DwingsInvoiceLookup"/>.
    /// Couvre l'extraction de tokens (BGI, BGPMT, Guarantee, EndToEndId), la résolution
    /// directe par identifiant, la résolution par garantie avec scoring date+montant,
    /// la détection d'ambiguïté, et la suggestion globale.
    /// </summary>
    public class DwingsLinkingHelperTests
    {
        // ============== Extraction de tokens ==============

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData("BGPMTABCD1234", "BGPMTABCD1234")]
        [InlineData("Pay BGPMTABCD1234 ref", "BGPMTABCD1234")]
        [InlineData("X BGPMTAB Y", null)]                  // moins de 8 chars après BGPMT
        public void ExtractBgpmtToken_VariousInputs(string input, string expected)
        {
            DwingsLinkingHelper.ExtractBgpmtToken(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("BGI202310ABCDEF1", "BGI202310ABCDEF1")] // YYYYMM + 7 hex
        [InlineData("ref BGI202310ABCDEF1 end", "BGI202310ABCDEF1")]
        [InlineData("BGI2310FRABCDEF1", "BGI2310FRABCDEF1")] // YYMM + country + 7 hex
        [InlineData("nothing here", null)]
        [InlineData(null, null)]
        public void ExtractBgiToken_VariousInputs(string input, string expected)
        {
            var got = DwingsLinkingHelper.ExtractBgiToken(input);
            if (expected == null)
                got.Should().BeNull();
            else
                got.Should().Be(expected.ToUpperInvariant());
        }

        [Theory]
        [InlineData("ref G1234FR123456789 end", "G1234FR123456789")]
        [InlineData("N5678XX987654321", "N5678XX987654321")]
        [InlineData("g1234fr123456789", null)] // Le pattern nécessite G/N majuscules
        [InlineData(null, null)]
        public void ExtractGuaranteeId_VariousInputs(string input, string expected)
        {
            DwingsLinkingHelper.ExtractGuaranteeId(input).Should().Be(expected);
        }

        [Theory]
        [InlineData("<pacs:EndToEndId>700.678</pacs:EndToEndId>", "700.678")]
        [InlineData("<EndToEndId>BGI20231024E1F84</EndToEndId>", "BGI20231024E1F84")]
        [InlineData("<EndToEndId>   ABC123   </EndToEndId>", "ABC123")]
        [InlineData("no tag here", null)]
        [InlineData(null, null)]
        public void ExtractEndToEndId_VariousInputs(string input, string expected)
        {
            DwingsLinkingHelper.ExtractEndToEndId(input).Should().Be(expected);
        }

        // ============== AmountMatches ==============

        [Theory]
        [InlineData(100.00, 100.00, true)]
        [InlineData(100.00, 100.005, true)]
        [InlineData(100.00, 100.02, false)]
        public void AmountMatches_DefaultTolerance(double a, double b, bool expected)
        {
            DwingsLinkingHelper.AmountMatches((decimal)a, (decimal)b).Should().Be(expected);
        }

        [Fact]
        public void AmountMatches_NullOperand_ReturnsFalse()
        {
            DwingsLinkingHelper.AmountMatches(null, 1m).Should().BeFalse();
            DwingsLinkingHelper.AmountMatches(1m, null).Should().BeFalse();
            DwingsLinkingHelper.AmountMatches(null, null).Should().BeFalse();
        }

        [Fact]
        public void AmountMatches_CustomTolerance()
        {
            DwingsLinkingHelper.AmountMatches(100m, 101m, tolerance: 1m).Should().BeTrue();
            DwingsLinkingHelper.AmountMatches(100m, 102m, tolerance: 1m).Should().BeFalse();
        }

        // ============== ResolveInvoiceByBgi / Bgpmt ==============

        [Fact]
        public void ResolveInvoiceByBgi_NullCollection_ReturnsNull()
        {
            DwingsLinkingHelper.ResolveInvoiceByBgi((IEnumerable<DwingsInvoiceDto>)null, "BGI1").Should().BeNull();
        }

        [Fact]
        public void ResolveInvoiceByBgi_EmptyOrWhitespaceKey_ReturnsNull()
        {
            var list = new List<DwingsInvoiceDto> { new DwingsInvoiceDto { INVOICE_ID = "BGI1" } };
            DwingsLinkingHelper.ResolveInvoiceByBgi(list, "").Should().BeNull();
            DwingsLinkingHelper.ResolveInvoiceByBgi(list, "   ").Should().BeNull();
        }

        [Fact]
        public void ResolveInvoiceByBgi_CaseInsensitiveAndTrim()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI20231024E1F84" };
            var list = new List<DwingsInvoiceDto> { inv };
            DwingsLinkingHelper.ResolveInvoiceByBgi(list, "  bgi20231024e1f84  ").Should().BeSameAs(inv);
        }

        [Fact]
        public void ResolveInvoiceByBgpmt_Match_ReturnsFirstHit()
        {
            var inv = new DwingsInvoiceDto { BGPMT = "BGPMTABCD1234" };
            var list = new List<DwingsInvoiceDto> { inv, new DwingsInvoiceDto { BGPMT = "OTHER" } };
            DwingsLinkingHelper.ResolveInvoiceByBgpmt(list, "BGPMTABCD1234").Should().BeSameAs(inv);
        }

        // ============== ResolveInvoicesByGuarantee ==============

        private static DwingsInvoiceDto MakeInv(
            string bcId,
            string status = "INITIATED",
            decimal? requested = null,
            decimal? billing = null,
            DateTime? requestedDate = null,
            string id = null)
        {
            return new DwingsInvoiceDto
            {
                INVOICE_ID = id ?? Guid.NewGuid().ToString("N"),
                BUSINESS_CASE_ID = bcId,
                T_INVOICE_STATUS = status,
                REQUESTED_AMOUNT = requested,
                BILLING_AMOUNT = billing,
                REQUESTED_EXECUTION_DATE = requestedDate
            };
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_EmptyKey_ReturnsEmpty()
        {
            DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                Enumerable.Empty<DwingsInvoiceDto>(), "", null, null)
                .Should().BeEmpty();
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_NoCandidate_ReturnsEmpty()
        {
            var list = new[] { MakeInv("OTHER") };
            DwingsLinkingHelper.ResolveInvoicesByGuarantee(list, "MISSING", null, null)
                .Should().BeEmpty();
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_NoInitiated_ReturnsEmpty()
        {
            // The "no initiated => no result" rule.
            var list = new[]
            {
                MakeInv("G1", status: "PAID",   requested: 100m),
                MakeInv("G1", status: "CLOSED", requested: 100m),
            };
            DwingsLinkingHelper.ResolveInvoicesByGuarantee(list, "G1", null, 100m)
                .Should().BeEmpty();
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_AmountFilter_RemovesNonMatching()
        {
            var list = new[]
            {
                MakeInv("G1", requested: 100m), // garde
                MakeInv("G1", requested: 999m), // exclu par filtre montant
            };
            var got = DwingsLinkingHelper.ResolveInvoicesByGuarantee(list, "G1", null, 100m);
            got.Should().HaveCount(1);
            got[0].REQUESTED_AMOUNT.Should().Be(100m);
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_ScoresByDateProximity()
        {
            var ambreDate = new DateTime(2024, 5, 10);
            var farInv = MakeInv("G1", requested: 100m, requestedDate: ambreDate.AddDays(-30), id: "FAR");
            var closeInv = MakeInv("G1", requested: 100m, requestedDate: ambreDate.AddDays(-1), id: "CLOSE");

            var got = DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                new[] { farInv, closeInv }, "G1", ambreDate, 100m);

            got.Should().HaveCount(2);
            got[0].INVOICE_ID.Should().Be("CLOSE");
            got[1].INVOICE_ID.Should().Be("FAR");
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_TakeOne_AmbiguousReturnsEmpty()
        {
            var ambreDate = new DateTime(2024, 5, 10);
            // Deux candidats à dates et montants identiques → ambigu, donc vide
            var a = MakeInv("G1", requested: 100m, requestedDate: ambreDate, id: "A");
            var b = MakeInv("G1", requested: 100m, requestedDate: ambreDate, id: "B");

            var got = DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                new[] { a, b }, "G1", ambreDate, 100m, take: 1);

            got.Should().BeEmpty();
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_PartialMatch_WhenNoExactBcId()
        {
            // Si aucun match exact mais un BUSINESS_CASE_REFERENCE contient la clé → fallback
            var inv = new DwingsInvoiceDto
            {
                INVOICE_ID = "I1",
                BUSINESS_CASE_REFERENCE = "PREFIX_G1234FR123456789_SUFFIX",
                T_INVOICE_STATUS = "INITIATED",
                REQUESTED_AMOUNT = 100m
            };
            var got = DwingsLinkingHelper.ResolveInvoicesByGuarantee(
                new[] { inv }, "G1234FR123456789", null, 100m);
            got.Should().ContainSingle().Which.Should().BeSameAs(inv);
        }

        // ============== SuggestInvoicesForAmbre ==============

        [Fact]
        public void SuggestInvoicesForAmbre_PrefersBgiOverBgpmtAndGuarantee()
        {
            var byBgi = new DwingsInvoiceDto { INVOICE_ID = "BGI20231024E1F84", T_INVOICE_STATUS = "INITIATED" };
            var byBgpmt = new DwingsInvoiceDto { INVOICE_ID = "X", BGPMT = "BGPMTABCD1234", T_INVOICE_STATUS = "INITIATED" };

            var got = DwingsLinkingHelper.SuggestInvoicesForAmbre(
                new[] { byBgi, byBgpmt },
                rawLabel: "BGI20231024E1F84 some BGPMTABCD1234",
                reconciliationNum: null,
                reconciliationOriginNum: null,
                explicitBgi: null,
                guaranteeId: null,
                ambreDate: null,
                ambreAmount: null,
                take: 5);

            got.Should().NotBeEmpty();
            got[0].INVOICE_ID.Should().Be("BGI20231024E1F84");
        }

        [Fact]
        public void SuggestInvoicesForAmbre_NoTokens_ReturnsEmpty()
        {
            var got = DwingsLinkingHelper.SuggestInvoicesForAmbre(
                new[] { new DwingsInvoiceDto { INVOICE_ID = "X" } },
                rawLabel: "no tokens here",
                reconciliationNum: null,
                reconciliationOriginNum: null,
                explicitBgi: null,
                guaranteeId: null,
                ambreDate: null,
                ambreAmount: null);
            got.Should().BeEmpty();
        }

        [Fact]
        public void SuggestInvoicesForAmbre_DeduplicatesAcrossStrategies()
        {
            // Une même facture trouvée via BGI puis Guarantee → ne doit apparaître qu'une fois
            var inv = new DwingsInvoiceDto
            {
                INVOICE_ID = "BGI20231024E1F84",
                BUSINESS_CASE_ID = "G1234FR123456789",
                T_INVOICE_STATUS = "INITIATED",
                REQUESTED_AMOUNT = 100m
            };
            var got = DwingsLinkingHelper.SuggestInvoicesForAmbre(
                new[] { inv },
                rawLabel: "BGI20231024E1F84 G1234FR123456789",
                reconciliationNum: null,
                reconciliationOriginNum: null,
                explicitBgi: null,
                guaranteeId: null,
                ambreDate: null,
                ambreAmount: 100m);
            got.Should().HaveCount(1);
            got[0].INVOICE_ID.Should().Be("BGI20231024E1F84");
        }
    }

    /// <summary>
    /// Tests pour la classe <see cref="DwingsInvoiceLookup"/> (lookups O(1)).
    /// </summary>
    public class DwingsInvoiceLookupTests
    {
        [Fact]
        public void Constructor_NullList_BuildsEmptyLookup()
        {
            var lk = new DwingsInvoiceLookup(null);
            lk.FindByInvoiceId("X").Should().BeNull();
            lk.FindByBgpmt("X").Should().BeNull();
            lk.FindByGuarantee("X").Should().BeNull();
        }

        [Fact]
        public void Constructor_IgnoresNullEntriesAndDuplicateKeys()
        {
            var i1 = new DwingsInvoiceDto { INVOICE_ID = "BGI1" };
            var i2 = new DwingsInvoiceDto { INVOICE_ID = "BGI1" }; // doublon → ignoré
            var lk = new DwingsInvoiceLookup(new[] { null, i1, i2 });
            lk.FindByInvoiceId("BGI1").Should().BeSameAs(i1);
        }

        [Fact]
        public void FindByInvoiceId_CaseInsensitive_AndTrims()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI20231024E1F84" };
            var lk = new DwingsInvoiceLookup(new[] { inv });
            lk.FindByInvoiceId(" bgi20231024e1f84 ").Should().BeSameAs(inv);
        }

        [Fact]
        public void FindByBgpmt_NullOrWhitespace_ReturnsNull()
        {
            var lk = new DwingsInvoiceLookup(new[] { new DwingsInvoiceDto { BGPMT = "B" } });
            lk.FindByBgpmt(null).Should().BeNull();
            lk.FindByBgpmt("   ").Should().BeNull();
        }

        [Fact]
        public void FindByGuarantee_IndexesBothBcReferenceAndBcId()
        {
            var inv = new DwingsInvoiceDto
            {
                INVOICE_ID = "I1",
                BUSINESS_CASE_REFERENCE = "REF1",
                BUSINESS_CASE_ID = "BC1"
            };
            var lk = new DwingsInvoiceLookup(new[] { inv });
            lk.FindByGuarantee("REF1").Should().ContainSingle().Which.Should().BeSameAs(inv);
            lk.FindByGuarantee("BC1").Should().ContainSingle().Which.Should().BeSameAs(inv);
        }

        [Fact]
        public void FindByGuarantee_MultipleInvoicesUnderSameKey()
        {
            var i1 = new DwingsInvoiceDto { INVOICE_ID = "A", BUSINESS_CASE_ID = "BC1" };
            var i2 = new DwingsInvoiceDto { INVOICE_ID = "B", BUSINESS_CASE_ID = "BC1" };
            var lk = new DwingsInvoiceLookup(new[] { i1, i2 });
            lk.FindByGuarantee("BC1").Should().HaveCount(2);
        }

        [Fact]
        public void ResolveInvoicesByGuarantee_LookupOverload_FiltersInitiatedAndAmount()
        {
            var ambreDate = new DateTime(2024, 5, 10);
            var i1 = new DwingsInvoiceDto
            {
                INVOICE_ID = "OK",
                BUSINESS_CASE_ID = "G1",
                T_INVOICE_STATUS = "INITIATED",
                REQUESTED_AMOUNT = 100m,
                REQUESTED_EXECUTION_DATE = ambreDate
            };
            var i2 = new DwingsInvoiceDto
            {
                INVOICE_ID = "BAD_AMOUNT",
                BUSINESS_CASE_ID = "G1",
                T_INVOICE_STATUS = "INITIATED",
                REQUESTED_AMOUNT = 999m
            };
            var i3 = new DwingsInvoiceDto
            {
                INVOICE_ID = "BAD_STATUS",
                BUSINESS_CASE_ID = "G1",
                T_INVOICE_STATUS = "PAID",
                REQUESTED_AMOUNT = 100m
            };
            var lk = new DwingsInvoiceLookup(new[] { i1, i2, i3 });

            var got = DwingsLinkingHelper.ResolveInvoicesByGuarantee(lk, "G1", ambreDate, 100m);
            got.Should().ContainSingle().Which.INVOICE_ID.Should().Be("OK");
        }

        [Fact]
        public void SuggestInvoicesForAmbre_LookupOverload_NullLookup_ReturnsEmpty()
        {
            var got = DwingsLinkingHelper.SuggestInvoicesForAmbre(
                lookup: null,
                rawLabel: "BGI20231024E1F84",
                reconciliationNum: null,
                reconciliationOriginNum: null,
                explicitBgi: null,
                guaranteeId: null,
                ambreDate: null,
                ambreAmount: null);
            got.Should().BeEmpty();
        }

        [Fact]
        public void SuggestInvoicesForAmbre_LookupOverload_FindsByBgi()
        {
            var inv = new DwingsInvoiceDto { INVOICE_ID = "BGI20231024E1F84" };
            var lk = new DwingsInvoiceLookup(new[] { inv });

            var got = DwingsLinkingHelper.SuggestInvoicesForAmbre(
                lookup: lk,
                rawLabel: "blah BGI20231024E1F84 blah",
                reconciliationNum: null,
                reconciliationOriginNum: null,
                explicitBgi: null,
                guaranteeId: null,
                ambreDate: null,
                ambreAmount: null);

            got.Should().ContainSingle().Which.Should().BeSameAs(inv);
        }
    }
}

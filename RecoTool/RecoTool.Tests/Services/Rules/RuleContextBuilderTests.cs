using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.DTOs;
using RecoTool.Services.Rules;
using Xunit;

namespace RecoTool.Tests.Services.Rules
{
    /// <summary>
    /// Tests pour <see cref="RuleContextBuilder"/> exploitant les interfaces
    /// <see cref="IReconciliationService"/> + <see cref="IOfflineFirstService"/>.
    /// Couvre :
    ///   • <see cref="RuleContextBuilder.CalculateGroupingFlagsBatch"/> (logique pure)
    ///   • <see cref="RuleContextBuilder.BuildAsync"/> sur les chemins où isGrouped/isAmountMatch
    ///     sont fournis explicitement (évite l'accès DB de CalculateGroupingFlagsAsync)
    /// </summary>
    public class RuleContextBuilderTests
    {
        private const string CountryId = "FR";
        private const string PivotAcc = "PIVOT_FR";
        private const string ReceivAcc = "RECV_FR";

        private static Country MakeCountry() => new Country
        {
            CNT_Id = CountryId,
            CNT_AmbrePivot = PivotAcc,
            CNT_AmbreReceivable = ReceivAcc
        };

        // ===== CalculateGroupingFlagsBatch (logique pure) =====

        [Fact]
        public void CalculateGroupingFlagsBatch_NullOrEmpty_ReturnsEmpty()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            sut.CalculateGroupingFlagsBatch(null, MakeCountry()).Should().BeEmpty();
            sut.CalculateGroupingFlagsBatch(new List<DataAmbre>(), MakeCountry()).Should().BeEmpty();
        }

        [Fact]
        public void CalculateGroupingFlagsBatch_BothSidesPresent_AmountsBalanced_IsGroupedAndMatched()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var lines = new List<DataAmbre>
            {
                new DataAmbre { ID = "P1", Account_ID = PivotAcc, SignedAmount = 100m },
                new DataAmbre { ID = "R1", Account_ID = ReceivAcc, SignedAmount = -100m }
            };

            var got = sut.CalculateGroupingFlagsBatch(lines, MakeCountry());
            got.Should().HaveCount(2);
            got["P1"].isGrouped.Should().BeTrue();
            got["P1"].isAmountMatch.Should().BeTrue();
            got["P1"].missingAmount.Should().Be(0m);
            got["R1"].isGrouped.Should().BeTrue();
        }

        [Fact]
        public void CalculateGroupingFlagsBatch_Imbalance_IsGroupedButNotAmountMatch()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var lines = new List<DataAmbre>
            {
                new DataAmbre { ID = "P1", Account_ID = PivotAcc, SignedAmount = 100m },
                new DataAmbre { ID = "R1", Account_ID = ReceivAcc, SignedAmount = -90m }
            };

            var got = sut.CalculateGroupingFlagsBatch(lines, MakeCountry());
            got["P1"].isGrouped.Should().BeTrue();
            got["P1"].isAmountMatch.Should().BeFalse();
            got["P1"].missingAmount.Should().Be(10m);
        }

        [Fact]
        public void CalculateGroupingFlagsBatch_OnlyOneSide_NotGrouped()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var lines = new List<DataAmbre>
            {
                new DataAmbre { ID = "P1", Account_ID = PivotAcc, SignedAmount = 100m },
                new DataAmbre { ID = "P2", Account_ID = PivotAcc, SignedAmount = 50m }
            };
            var got = sut.CalculateGroupingFlagsBatch(lines, MakeCountry());
            got["P1"].isGrouped.Should().BeFalse();
            got["P2"].isGrouped.Should().BeFalse();
            got["P1"].missingAmount.Should().BeNull();
        }

        [Fact]
        public void CalculateGroupingFlagsBatch_AmountTolerance_OneCent()
        {
            // |missing| < 0.01 → isAmountMatch true
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var lines = new List<DataAmbre>
            {
                new DataAmbre { ID = "P", Account_ID = PivotAcc, SignedAmount = 100m },
                new DataAmbre { ID = "R", Account_ID = ReceivAcc, SignedAmount = -99.999m }
            };
            var got = sut.CalculateGroupingFlagsBatch(lines, MakeCountry());
            got["P"].isAmountMatch.Should().BeTrue();
        }

        // ===== BuildAsync =====

        private static Reconciliation MakeReco(string id = "X") => new Reconciliation { ID = id };

        [Fact]
        public async Task BuildAsync_PivotPath_DoesNotQueryReconciliationServiceForGuarantee()
        {
            var reco = new Mock<IReconciliationService>(MockBehavior.Strict);
            // Pivot + isGrouped/isAmountMatch fournis → aucune méthode appelée
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var ambre = new DataAmbre { ID = "A1", Account_ID = PivotAcc, SignedAmount = 100m, RawLabel = "BGI20240101ABCDEF1" };
            var ctx = await sut.BuildAsync(ambre, MakeReco(), MakeCountry(), CountryId, isPivot: true,
                isGrouped: true, isAmountMatch: true);

            ctx.Should().NotBeNull();
            ctx.IsPivot.Should().BeTrue();
            ctx.CountryId.Should().Be(CountryId);
            ctx.IsGrouped.Should().BeTrue();
            ctx.IsAmountMatch.Should().BeTrue();
            ctx.Sign.Should().Be("C"); // SignedAmount > 0 → crédit
            ctx.GuaranteeType.Should().BeNull(); // Pivot path skip
            ctx.TransactionType.Should().Be(TransactionType.TRIGGER.ToString()); // BGI in label → TRIGGER
        }

        [Fact]
        public async Task BuildAsync_NegativeSignedAmount_SignIsDebit()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var ambre = new DataAmbre { ID = "A2", SignedAmount = -50m, Account_ID = PivotAcc, RawLabel = "" };
            var ctx = await sut.BuildAsync(ambre, MakeReco(), MakeCountry(), CountryId, isPivot: true,
                isGrouped: false, isAmountMatch: false);

            ctx.Sign.Should().Be("D");
        }

        [Fact]
        public async Task BuildAsync_DwingsLink_ResolvesMtAndCommAndStatus()
        {
            var inv = new DwingsInvoiceDto
            {
                INVOICE_ID = "BGI1",
                MT_STATUS = "ACKED",
                COMM_ID_EMAIL = true,
                T_INVOICE_STATUS = "INITIATED",
                T_PAYMENT_REQUEST_STATUS = "REQUESTED"
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto> { inv });
            reco.Setup(x => x.GetDwingsGuaranteesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsGuaranteeDto>)new List<DwingsGuaranteeDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var ambre = new DataAmbre { ID = "A3", SignedAmount = 100m, Account_ID = ReceivAcc };
            var r = MakeReco();
            r.DWINGS_InvoiceID = "BGI1";

            var ctx = await sut.BuildAsync(ambre, r, MakeCountry(), CountryId, isPivot: false,
                isGrouped: true, isAmountMatch: true);

            ctx.MtStatus.Should().Be("ACKED");
            ctx.HasCommIdEmail.Should().BeTrue();
            ctx.IsBgiInitiated.Should().BeTrue();
            ctx.InvoiceStatus.Should().Be("INITIATED");
            ctx.PaymentRequestStatus.Should().Be("REQUESTED");
            ctx.Bgi.Should().Be("BGI1");
            ctx.HasDwingsLink.Should().BeTrue();
        }

        [Fact]
        public async Task BuildAsync_HasDwingsLink_TrueWhenAnyDwingsRefSet()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());
            reco.Setup(x => x.GetDwingsGuaranteesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsGuaranteeDto>)new List<DwingsGuaranteeDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            // Cas 1 : DWINGS_BGPMT seul
            var r1 = new Reconciliation { ID = "X", DWINGS_BGPMT = "BGPMT1" };
            var ctx1 = await sut.BuildAsync(new DataAmbre { Account_ID = PivotAcc },
                r1, MakeCountry(), CountryId, true, isGrouped: false, isAmountMatch: false);
            ctx1.HasDwingsLink.Should().BeTrue();

            // Cas 2 : InternalInvoiceReference seul
            var r2 = new Reconciliation { ID = "Y", InternalInvoiceReference = "REF42" };
            var ctx2 = await sut.BuildAsync(new DataAmbre { Account_ID = PivotAcc },
                r2, MakeCountry(), CountryId, true, isGrouped: false, isAmountMatch: false);
            ctx2.HasDwingsLink.Should().BeTrue();

            // Cas 3 : aucune référence
            var r3 = new Reconciliation { ID = "Z" };
            var ctx3 = await sut.BuildAsync(new DataAmbre { Account_ID = PivotAcc },
                r3, MakeCountry(), CountryId, true, isGrouped: false, isAmountMatch: false);
            ctx3.HasDwingsLink.Should().BeFalse();
        }

        [Fact]
        public async Task BuildAsync_TriggerDate_DerivesFlagsCorrectly()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var r = new Reconciliation { ID = "X", TriggerDate = DateTime.Today.AddDays(-5) };
            var ctx = await sut.BuildAsync(new DataAmbre { Account_ID = PivotAcc },
                r, MakeCountry(), CountryId, true, isGrouped: false, isAmountMatch: false);

            ctx.TriggerDateIsNull.Should().BeFalse();
            ctx.DaysSinceTrigger.Should().Be(5);
        }

        [Fact]
        public async Task BuildAsync_NoTriggerDate_DerivesNullFlag()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var r = new Reconciliation { ID = "X" }; // pas de TriggerDate
            var ctx = await sut.BuildAsync(new DataAmbre { Account_ID = PivotAcc },
                r, MakeCountry(), CountryId, true, isGrouped: false, isAmountMatch: false);

            ctx.TriggerDateIsNull.Should().BeTrue();
            ctx.DaysSinceTrigger.Should().BeNull();
        }

        [Fact]
        public async Task BuildAsync_OperationDate_DerivesDaysAgo()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var ambre = new DataAmbre { Account_ID = PivotAcc, Operation_Date = DateTime.Today.AddDays(-10) };
            var ctx = await sut.BuildAsync(ambre, MakeReco(), MakeCountry(), CountryId, true,
                isGrouped: false, isAmountMatch: false);

            ctx.OperationDaysAgo.Should().Be(10);
        }

        [Fact]
        public async Task BuildAsync_IsNewLine_TrueWhenCreatedToday()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var ambre = new DataAmbre { Account_ID = PivotAcc };
            // CreationDate is set to DateTime.Now in BaseEntity ctor
            var ctx = await sut.BuildAsync(ambre, MakeReco(), MakeCountry(), CountryId, true,
                isGrouped: false, isAmountMatch: false);

            ctx.IsNewLine.Should().BeTrue();
        }

        [Fact]
        public async Task BuildAsync_GuaranteeType_LookedUpOnReceivablePath()
        {
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetDwingsInvoicesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsInvoiceDto>)new List<DwingsInvoiceDto>());
            reco.Setup(x => x.GetDwingsGuaranteesAsync())
                .ReturnsAsync((IReadOnlyList<DwingsGuaranteeDto>)new List<DwingsGuaranteeDto>
                {
                    new DwingsGuaranteeDto { GUARANTEE_ID = "G1", GUARANTEE_TYPE = "ISSU" }
                });

            var sut = new RuleContextBuilder(reco.Object, Mock.Of<IOfflineFirstService>());

            var r = new Reconciliation { ID = "X", DWINGS_GuaranteeID = "G1" };
            var ctx = await sut.BuildAsync(new DataAmbre { Account_ID = ReceivAcc },
                r, MakeCountry(), CountryId, isPivot: false, isGrouped: true, isAmountMatch: true);

            ctx.GuaranteeType.Should().Be("ISSU");
        }

        // ===== CalculateGroupingFlagsAsync (avec seam injecté) =====

        [Fact]
        public async Task CalculateGroupingFlagsAsync_NullRecoOrAmbre_ReturnsAllNull()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var (g, m, mm) = await sut.CalculateGroupingFlagsAsync(null, null, MakeCountry(), CountryId);
            g.Should().BeNull(); m.Should().BeNull(); mm.Should().BeNull();
        }

        [Fact]
        public async Task CalculateGroupingFlagsAsync_NoGroupingReference_ReturnsFalseFalseNull()
        {
            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), Mock.Of<IOfflineFirstService>());
            var r = new Reconciliation { ID = "X" }; // aucune référence DWINGS / interne
            var ambre = new DataAmbre { ID = "A" };

            var (g, m, mm) = await sut.CalculateGroupingFlagsAsync(ambre, r, MakeCountry(), CountryId);
            g.Should().BeFalse();
            m.Should().BeFalse();
            mm.Should().BeNull();
        }

        [Fact]
        public async Task CalculateGroupingFlagsAsync_MissingConnectionStrings_ReturnsAllNull()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetAmbreConnectionString(CountryId)).Returns((string)null);
            ofs.Setup(x => x.GetCountryConnectionString(CountryId)).Returns((string)null);

            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), ofs.Object);
            var r = new Reconciliation { ID = "A", DWINGS_BGPMT = "BGPMT1" };
            var ambre = new DataAmbre { ID = "A", Account_ID = PivotAcc, SignedAmount = 100m };

            var (g, m, mm) = await sut.CalculateGroupingFlagsAsync(ambre, r, MakeCountry(), CountryId);
            g.Should().BeNull(); m.Should().BeNull(); mm.Should().BeNull();
        }

        [Fact]
        public async Task CalculateGroupingFlagsAsync_BalancedGroup_ReturnsTrueTrueAndZero()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetAmbreConnectionString(CountryId)).Returns("any");
            ofs.Setup(x => x.GetCountryConnectionString(CountryId)).Returns("any");

            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), ofs.Object);

            // Faux loader : retourne deux lignes opposées (P=100, R=-100) partageant le groupingRef
            sut.__SetRelatedLinesLoaderForTesting((groupingRef, reconCs, ambreCs) =>
            {
                groupingRef.Should().Be("BGPMT1");
                return Task.FromResult(new List<DataAmbre>
                {
                    new DataAmbre { ID = "A", Account_ID = PivotAcc, SignedAmount = 100m },
                    new DataAmbre { ID = "B", Account_ID = ReceivAcc, SignedAmount = -100m }
                });
            });

            var r = new Reconciliation { ID = "A", DWINGS_BGPMT = "BGPMT1" };
            var ambre = new DataAmbre { ID = "A", Account_ID = PivotAcc, SignedAmount = 100m };

            var (g, m, mm) = await sut.CalculateGroupingFlagsAsync(ambre, r, MakeCountry(), CountryId);
            g.Should().BeTrue();
            m.Should().BeTrue();
            mm.Should().Be(0m);
        }

        [Fact]
        public async Task CalculateGroupingFlagsAsync_ImbalancedGroup_ReturnsTrueFalseAndDelta()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetAmbreConnectionString(CountryId)).Returns("any");
            ofs.Setup(x => x.GetCountryConnectionString(CountryId)).Returns("any");

            var sut = new RuleContextBuilder(Mock.Of<IReconciliationService>(), ofs.Object);

            sut.__SetRelatedLinesLoaderForTesting((groupingRef, reconCs, ambreCs) =>
                Task.FromResult(new List<DataAmbre>
                {
                    new DataAmbre { ID = "A", Account_ID = PivotAcc, SignedAmount = 100m },
                    new DataAmbre { ID = "B", Account_ID = ReceivAcc, SignedAmount = -90m }
                }));

            var r = new Reconciliation { ID = "A", DWINGS_BGPMT = "BGPMT1" };
            var ambre = new DataAmbre { ID = "A", Account_ID = PivotAcc, SignedAmount = 100m };

            var (g, m, mm) = await sut.CalculateGroupingFlagsAsync(ambre, r, MakeCountry(), CountryId);
            g.Should().BeTrue();
            m.Should().BeFalse();
            mm.Should().Be(10m);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests métier pour <see cref="ReconciliationMatchingService"/> exploitant les
    /// interfaces <see cref="IReconciliationService"/> et <see cref="IOfflineFirstService"/>
    /// extraites lors du refactor.
    ///
    /// Couvre :
    ///   • PerformAutomaticMatchingAsync — matching Receivable→Pivot via invoice ID
    ///     contenu dans le label MBAW pivot.
    ///   • ApplyManualOutgoingRuleAsync — appariement de paires pivot/pivot avec
    ///     guarantee identique et montants nuls en somme.
    /// </summary>
    public class ReconciliationMatchingServiceTests
    {
        private const string CountryId = "FR";
        private const string PivotAcc = "PIVOT_FR";
        private const string ReceivAcc = "RECV_FR";

        private static readonly Dictionary<string, Country> Countries = new Dictionary<string, Country>
        {
            { CountryId, new Country
                {
                    CNT_Id = CountryId,
                    CNT_AmbrePivot = PivotAcc,
                    CNT_AmbreReceivable = ReceivAcc
                }
            }
        };

        private static DataAmbre Pivot(string id, string mbawLabel = null) => new DataAmbre
        {
            ID = id, Account_ID = PivotAcc, Pivot_MbawIDFromLabel = mbawLabel
        };

        private static DataAmbre Recv(string id, string invoiceFromAmbre = null) => new DataAmbre
        {
            ID = id, Account_ID = ReceivAcc, Receivable_InvoiceFromAmbre = invoiceFromAmbre
        };

        // ===== Ctor guards =====

        [Fact]
        public void Ctor_NullReconciliationService_Throws()
        {
            Action a = () => new ReconciliationMatchingService(null, Mock.Of<IOfflineFirstService>(), "alice");
            a.Should().Throw<ArgumentNullException>();
        }

        [Fact]
        public void Ctor_NullOfs_Allowed()
        {
            Action a = () => new ReconciliationMatchingService(Mock.Of<IReconciliationService>(), null, "alice");
            a.Should().NotThrow();
        }

        // ===== PerformAutomaticMatchingAsync =====

        [Fact]
        public async Task PerformAutomaticMatchingAsync_UnknownCountry_ReturnsZero()
        {
            var reco = new Mock<IReconciliationService>(MockBehavior.Strict);
            // aucune méthode ne doit être appelée
            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");

            var n = await sut.PerformAutomaticMatchingAsync("ZZ", Countries);
            n.Should().Be(0);

            reco.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task PerformAutomaticMatchingAsync_NoMatches_ReturnsZeroAndDoesNotSave()
        {
            var ambre = new List<DataAmbre>
            {
                Recv("R1", "INV1"),
                Pivot("P1", mbawLabel: "OTHERREF"),
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);

            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");
            var n = await sut.PerformAutomaticMatchingAsync(CountryId, Countries);

            n.Should().Be(0);
            reco.Verify(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Fact]
        public async Task PerformAutomaticMatchingAsync_OneReceivableMatchesOnePivot_SavesAndReturnsOne()
        {
            var ambre = new List<DataAmbre>
            {
                Recv("R1", "INV1"),
                Pivot("P1", mbawLabel: "stuff INV1 stuff"),
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);
            reco.Setup(x => x.GetOrCreateReconciliationAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => Reconciliation.CreateForAmbreLine(id));
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .ReturnsAsync(true);

            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");
            var n = await sut.PerformAutomaticMatchingAsync(CountryId, Countries);

            n.Should().Be(1);
            reco.Verify(x => x.GetOrCreateReconciliationAsync("R1"), Times.Once);
            reco.Verify(x => x.GetOrCreateReconciliationAsync("P1"), Times.Once);
            reco.Verify(x => x.SaveReconciliationsAsync(
                It.Is<IEnumerable<Reconciliation>>(seq => seq.Count() == 2),
                It.IsAny<bool>()),
                Times.Once);
        }

        [Fact]
        public async Task PerformAutomaticMatchingAsync_MultiplePivotsMatchOneReceivable_AllPersisted()
        {
            var ambre = new List<DataAmbre>
            {
                Recv("R1", "INV1"),
                Pivot("P1", "ref INV1 abc"),
                Pivot("P2", "INV1xyz"),
                Pivot("P3", "no match"),
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);
            reco.Setup(x => x.GetOrCreateReconciliationAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => Reconciliation.CreateForAmbreLine(id));
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .ReturnsAsync(true);

            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");
            var n = await sut.PerformAutomaticMatchingAsync(CountryId, Countries);

            n.Should().Be(1);
            // R1 + P1 + P2 → 3 reconciliations sauvegardées
            reco.Verify(x => x.SaveReconciliationsAsync(
                It.Is<IEnumerable<Reconciliation>>(seq => seq.Count() == 3),
                It.IsAny<bool>()),
                Times.Once);
            reco.Verify(x => x.GetOrCreateReconciliationAsync("P3"), Times.Never);
        }

        [Fact]
        public async Task PerformAutomaticMatchingAsync_SkipsReceivablesWithoutInvoice()
        {
            var ambre = new List<DataAmbre>
            {
                Recv("R_empty"),
                Recv("R1", "INV1"),
                Pivot("P1", "INV1"),
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);
            reco.Setup(x => x.GetOrCreateReconciliationAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => Reconciliation.CreateForAmbreLine(id));
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .ReturnsAsync(true);

            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");
            var n = await sut.PerformAutomaticMatchingAsync(CountryId, Countries);

            n.Should().Be(1); // R1 seul produit un match
            reco.Verify(x => x.GetOrCreateReconciliationAsync("R_empty"), Times.Never);
        }

        [Fact]
        public async Task PerformAutomaticMatchingAsync_AppendsAuditCommentToReconciliations()
        {
            var ambre = new List<DataAmbre>
            {
                Recv("R1", "INV1"),
                Pivot("P1", "INV1"),
            };
            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);
            reco.Setup(x => x.GetOrCreateReconciliationAsync(It.IsAny<string>()))
                .ReturnsAsync((string id) => Reconciliation.CreateForAmbreLine(id));

            IEnumerable<Reconciliation> captured = null;
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .Callback<IEnumerable<Reconciliation>, bool>((seq, _) => captured = seq.ToList())
                .ReturnsAsync(true);

            var sut = new ReconciliationMatchingService(reco.Object, Mock.Of<IOfflineFirstService>(), "alice");
            await sut.PerformAutomaticMatchingAsync(CountryId, Countries);

            captured.Should().NotBeNull();
            var list = captured.ToList();
            list[0].Comments.Should().Contain("Auto-matched with 1 pivot line(s)");
            list[0].Comments.Should().Contain("alice");
            list[1].Comments.Should().Contain("Auto-matched with receivable line R1");
        }

        // ===== ApplyManualOutgoingRuleAsync =====

        [Fact]
        public async Task ApplyManualOutgoingRule_UnknownCountry_ReturnsZero()
        {
            var sut = new ReconciliationMatchingService(
                Mock.Of<IReconciliationService>(),
                Mock.Of<IOfflineFirstService>(),
                "alice");

            (await sut.ApplyManualOutgoingRuleAsync("ZZ", Countries)).Should().Be(0);
        }

        [Fact]
        public async Task ApplyManualOutgoingRule_NullUserFields_ReturnsZero()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.UserFields).Returns((List<UserField>)null);

            var sut = new ReconciliationMatchingService(
                Mock.Of<IReconciliationService>(),
                ofs.Object,
                "alice");

            (await sut.ApplyManualOutgoingRuleAsync(CountryId, Countries)).Should().Be(0);
        }

        [Fact]
        public async Task ApplyManualOutgoingRule_MissingActionOrKpi_ReturnsZero()
        {
            // Cas 1 : pas d'action MATCH
            var ofs1 = new Mock<IOfflineFirstService>();
            ofs1.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "PAID BUT NOT RECONCILED" }
            });

            var sut1 = new ReconciliationMatchingService(
                Mock.Of<IReconciliationService>(),
                ofs1.Object,
                "alice");

            (await sut1.ApplyManualOutgoingRuleAsync(CountryId, Countries)).Should().Be(0);

            // Cas 2 : pas de KPI PAID BUT NOT RECONCILED
            var ofs2 = new Mock<IOfflineFirstService>();
            ofs2.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "MATCH" }
            });

            var sut2 = new ReconciliationMatchingService(
                Mock.Of<IReconciliationService>(),
                ofs2.Object,
                "alice");

            (await sut2.ApplyManualOutgoingRuleAsync(CountryId, Countries)).Should().Be(0);
        }

        [Fact]
        public async Task ApplyManualOutgoingRule_PairsThatSumToZero_AreMatched()
        {
            // Setup : 2 lignes pivot avec même garantie, montants opposés
            var pivotA = Pivot("PA"); pivotA.SignedAmount = 100m;
            var pivotB = Pivot("PB"); pivotB.SignedAmount = -100m;

            var ambre = new List<DataAmbre> { pivotA, pivotB };

            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "MATCH" },
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "PAID BUT NOT RECONCILED" }
            });

            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false)).ReturnsAsync(ambre);

            var recoA = Reconciliation.CreateForAmbreLine("PA");
            recoA.DWINGS_GuaranteeID = "GUAR1";
            var recoB = Reconciliation.CreateForAmbreLine("PB");
            recoB.DWINGS_GuaranteeID = "GUAR1";

            reco.Setup(x => x.GetOrCreateReconciliationAsync("PA")).ReturnsAsync(recoA);
            reco.Setup(x => x.GetOrCreateReconciliationAsync("PB")).ReturnsAsync(recoB);

            int saveCalls = 0;
            reco.Setup(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()))
                .Callback(() => saveCalls++)
                .ReturnsAsync(true);

            var sut = new ReconciliationMatchingService(reco.Object, ofs.Object, "alice");
            var n = await sut.ApplyManualOutgoingRuleAsync(CountryId, Countries);

            n.Should().Be(1, "une paire trouvée");
            saveCalls.Should().Be(1);

            recoA.Action.Should().Be(5);
            recoA.KPI.Should().Be(18);
            recoB.Action.Should().Be(5);
            recoB.KPI.Should().Be(18);

            recoA.Comments.Should().Contain("Same guarantee Pair detected");
            recoB.Comments.Should().Contain("Same guarantee Pair detected");
        }

        [Fact]
        public async Task ApplyManualOutgoingRule_NoSumZero_NotMatched()
        {
            var pivotA = Pivot("PA"); pivotA.SignedAmount = 100m;
            var pivotB = Pivot("PB"); pivotB.SignedAmount = 50m; // ne somme pas à 0

            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "MATCH" },
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "PAID BUT NOT RECONCILED" }
            });

            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false))
                .ReturnsAsync(new List<DataAmbre> { pivotA, pivotB });
            var recoA = Reconciliation.CreateForAmbreLine("PA"); recoA.DWINGS_GuaranteeID = "G";
            var recoB = Reconciliation.CreateForAmbreLine("PB"); recoB.DWINGS_GuaranteeID = "G";
            reco.Setup(x => x.GetOrCreateReconciliationAsync("PA")).ReturnsAsync(recoA);
            reco.Setup(x => x.GetOrCreateReconciliationAsync("PB")).ReturnsAsync(recoB);

            var sut = new ReconciliationMatchingService(reco.Object, ofs.Object, "alice");
            var n = await sut.ApplyManualOutgoingRuleAsync(CountryId, Countries);

            n.Should().Be(0);
            reco.Verify(x => x.SaveReconciliationsAsync(It.IsAny<IEnumerable<Reconciliation>>(), It.IsAny<bool>()),
                Times.Never);
        }

        [Fact]
        public async Task ApplyManualOutgoingRule_DifferentGuarantee_NotMatched()
        {
            var pivotA = Pivot("PA"); pivotA.SignedAmount = 100m;
            var pivotB = Pivot("PB"); pivotB.SignedAmount = -100m;

            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.UserFields).Returns(new List<UserField>
            {
                new UserField { USR_ID = 5, USR_Category = "Action", USR_FieldName = "MATCH" },
                new UserField { USR_ID = 18, USR_Category = "KPI", USR_FieldName = "PAID BUT NOT RECONCILED" }
            });

            var reco = new Mock<IReconciliationService>();
            reco.Setup(x => x.GetAmbreDataAsync(CountryId, false))
                .ReturnsAsync(new List<DataAmbre> { pivotA, pivotB });
            var recoA = Reconciliation.CreateForAmbreLine("PA"); recoA.DWINGS_GuaranteeID = "G_AAA";
            var recoB = Reconciliation.CreateForAmbreLine("PB"); recoB.DWINGS_GuaranteeID = "G_BBB"; // différent
            reco.Setup(x => x.GetOrCreateReconciliationAsync("PA")).ReturnsAsync(recoA);
            reco.Setup(x => x.GetOrCreateReconciliationAsync("PB")).ReturnsAsync(recoB);

            var sut = new ReconciliationMatchingService(reco.Object, ofs.Object, "alice");
            (await sut.ApplyManualOutgoingRuleAsync(CountryId, Countries)).Should().Be(0);
        }
    }
}

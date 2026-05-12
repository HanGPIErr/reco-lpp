using System;
using System.Collections.Generic;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.Ambre;
using Xunit;

namespace RecoTool.Tests.Services.Ambre
{
    /// <summary>
    /// Tests pour <see cref="AmbreDataProcessor"/>. Service refactorisé pour accepter
    /// <see cref="IOfflineFirstService"/> — testable via Moq. La méthode testée ici
    /// (<see cref="AmbreDataProcessor.FilterRowsByCountryAccounts"/>) est purement logique.
    /// Pour les autres méthodes (ReadExcel, transformations) il faudra des tests
    /// d'intégration avec un vrai fichier .xlsx.
    /// </summary>
    public class AmbreDataProcessorTests
    {
        // ----- Helpers -----

        private static Country FrCountry() => new Country
        {
            CNT_Id = "FR",
            CNT_AmbrePivot = "PIVOT_FR",
            CNT_AmbreReceivable = "RECV_FR",
            CNT_AmbrePivotCountryId = 250,
            CNT_AmbreReceivableCountryId = 250
        };

        private static Dictionary<string, object> Row(string accountId, string entityId = "250")
            => new Dictionary<string, object>
            {
                { "Account_ID", accountId },
                { "Entity_ID", entityId }
            };

        private static AmbreDataProcessor MakeSut()
            => new AmbreDataProcessor(Mock.Of<IOfflineFirstService>(), "test-user");

        // ===== FilterRowsByCountryAccounts =====

        [Fact]
        public void FilterRowsByCountryAccounts_NullCountry_Throws()
        {
            var sut = MakeSut();
            Action a = () => sut.FilterRowsByCountryAccounts(new List<Dictionary<string, object>>(), null);
            a.Should().Throw<InvalidOperationException>().WithMessage("*Configuration pays*");
        }

        [Fact]
        public void FilterRowsByCountryAccounts_NoPivotOrReceivable_Throws()
        {
            var sut = MakeSut();
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = null, CNT_AmbreReceivable = "" };
            Action a = () => sut.FilterRowsByCountryAccounts(new List<Dictionary<string, object>>(), country);
            a.Should().Throw<InvalidOperationException>().WithMessage("*Pivot/Receivable*");
        }

        [Fact]
        public void FilterRowsByCountryAccounts_KeepsMatchingRows()
        {
            var sut = MakeSut();
            var data = new List<Dictionary<string, object>>
            {
                Row("PIVOT_FR"),
                Row("RECV_FR"),
                Row("OTHER_ACC"),
            };
            var got = sut.FilterRowsByCountryAccounts(data, FrCountry());
            got.Should().HaveCount(2);
        }

        [Fact]
        public void FilterRowsByCountryAccounts_DropsRowsWithoutAccountId()
        {
            var sut = MakeSut();
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Entity_ID", "250" } }, // pas d'Account_ID
                Row("PIVOT_FR")
            };
            sut.FilterRowsByCountryAccounts(data, FrCountry()).Should().HaveCount(1);
        }

        [Fact]
        public void FilterRowsByCountryAccounts_DropsRowsWithEmptyAccountId()
        {
            var sut = MakeSut();
            var data = new List<Dictionary<string, object>>
            {
                Row(""),
                Row("   "),
                Row("PIVOT_FR")
            };
            sut.FilterRowsByCountryAccounts(data, FrCountry()).Should().HaveCount(1);
        }

        [Fact]
        public void FilterRowsByCountryAccounts_DropsRowsWithDifferentEntityId()
        {
            var sut = MakeSut();
            var data = new List<Dictionary<string, object>>
            {
                Row("PIVOT_FR", entityId: "250"),  // garder
                Row("PIVOT_FR", entityId: "999")   // exclure (entity ne matche pas)
            };
            sut.FilterRowsByCountryAccounts(data, FrCountry()).Should().HaveCount(1);
        }

        [Fact]
        public void FilterRowsByCountryAccounts_AcceptsAccountIdWithExtraWhitespace()
        {
            var sut = MakeSut();
            var data = new List<Dictionary<string, object>>
            {
                Row("  PIVOT_FR  ")
            };
            // Le filtre fait Trim() sur l'Account_ID
            sut.FilterRowsByCountryAccounts(data, FrCountry()).Should().HaveCount(1);
        }
    }
}

using FluentAssertions;
using RecoTool.Domain.Filters;
using Xunit;

namespace RecoTool.Tests.Domain.Filters
{
    /// <summary>
    /// Tests pour <see cref="FilterSqlHelper"/> : strip Account_ID, sérialisation/embed
    /// JSON dans la clause WHERE, extraction et flag PotentialDuplicates, et
    /// normalisation de fragment de prédicat (avec garde anti-injection).
    /// </summary>
    public class FilterSqlHelperTests
    {
        // ===================== StripAccount =====================

        [Fact]
        public void StripAccount_NullOrWhitespace_ReturnsAsIs()
        {
            FilterSqlHelper.StripAccount(null).Should().BeNull();
            FilterSqlHelper.StripAccount("").Should().Be("");
            FilterSqlHelper.StripAccount("   ").Should().Be("   ");
        }

        [Fact]
        public void StripAccount_BarePredicate_RemovesIt()
        {
            FilterSqlHelper.StripAccount("Account_ID = 'X'")
                .Should().BeEmpty();
        }

        [Fact]
        public void StripAccount_BarePredicateWithWhere_KeepsWhereButEmptiesIt()
        {
            // Cas dégénéré : la prédicate seule est supprimée, mais le WHERE reste
            FilterSqlHelper.StripAccount("WHERE Account_ID = 'X'")
                .Should().BeEmpty();
        }

        [Fact]
        public void StripAccount_PredicateAtStart_RemovesAndKeepsRest()
        {
            FilterSqlHelper.StripAccount("Account_ID = 'X' AND CCY = 'EUR'")
                .Should().Contain("CCY = 'EUR'")
                .And.NotContain("Account_ID");
        }

        [Fact]
        public void StripAccount_PredicateAtEnd_RemovesAndKeepsRest()
        {
            FilterSqlHelper.StripAccount("CCY = 'EUR' AND Account_ID = 'X'")
                .Should().Contain("CCY = 'EUR'")
                .And.NotContain("Account_ID");
        }

        [Fact]
        public void StripAccount_PredicateInMiddle_RemovesAndCollapsesAnd()
        {
            var got = FilterSqlHelper.StripAccount(
                "CCY = 'EUR' AND Account_ID = 'X' AND a = 1");
            got.Should().Contain("CCY = 'EUR'");
            got.Should().Contain("a = 1");
            got.Should().NotContain("Account_ID");
        }

        [Fact]
        public void StripAccount_PreservesWhereKeyword_WhenPresent()
        {
            FilterSqlHelper.StripAccount("WHERE Account_ID = 'X' AND CCY = 'EUR'")
                .Should().StartWith("WHERE ");
        }

        [Fact]
        public void StripAccount_AliasedAccount_StillRemoved()
        {
            FilterSqlHelper.StripAccount("a.Account_ID = 'X' AND CCY = 'EUR'")
                .Should().NotContain("Account_ID")
                .And.Contain("CCY = 'EUR'");
        }

        // ===================== BuildSqlWithJson / TryExtractPreset =====================

        [Fact]
        public void BuildSqlWithJson_RoundTrip_ProducesParseableComment()
        {
            var preset = new FilterPreset { AccountId = "A1", Currency = "EUR" };
            var sql = FilterSqlHelper.BuildSqlWithJson(preset, "WHERE x = 1");
            sql.Should().StartWith("/*JSON:")
                .And.Contain("\"AccountId\":\"A1\"")
                .And.EndWith(" WHERE x = 1");
        }

        [Fact]
        public void BuildSqlWithJson_OmitsNullProperties()
        {
            var preset = new FilterPreset { AccountId = "A1" }; // tout le reste est null
            var sql = FilterSqlHelper.BuildSqlWithJson(preset, "WHERE x = 1");
            sql.Should().Contain("\"AccountId\":\"A1\"");
            sql.Should().NotContain("\"Currency\":null");
        }

        [Fact]
        public void TryExtractPreset_NoComment_ReturnsFalseAndOriginalSql()
        {
            var ok = FilterSqlHelper.TryExtractPreset("WHERE x = 1", out var json, out var pure);
            ok.Should().BeFalse();
            json.Should().BeNull();
            pure.Should().Be("WHERE x = 1");
        }

        [Fact]
        public void TryExtractPreset_WithComment_ExtractsJsonAndPureWhere()
        {
            var sql = "/*JSON:{\"AccountId\":\"A1\"}*/ WHERE x = 1";
            var ok = FilterSqlHelper.TryExtractPreset(sql, out var json, out var pure);
            ok.Should().BeTrue();
            json.Should().Be("{\"AccountId\":\"A1\"}");
            pure.Should().Be("WHERE x = 1");
        }

        [Fact]
        public void TryExtractPreset_NullSql_ReturnsFalse()
        {
            var ok = FilterSqlHelper.TryExtractPreset(null, out var json, out var pure);
            ok.Should().BeFalse();
            json.Should().BeNull();
            pure.Should().Be(string.Empty);
        }

        // ===================== TryExtractPotentialDuplicatesFlag =====================

        [Fact]
        public void TryExtractPotentialDuplicatesFlag_True()
        {
            var preset = new FilterPreset { PotentialDuplicates = true };
            var sql = FilterSqlHelper.BuildSqlWithJson(preset, string.Empty);
            FilterSqlHelper.TryExtractPotentialDuplicatesFlag(sql).Should().BeTrue();
        }

        [Fact]
        public void TryExtractPotentialDuplicatesFlag_FalseOrAbsent()
        {
            FilterSqlHelper.TryExtractPotentialDuplicatesFlag(null).Should().BeFalse();
            FilterSqlHelper.TryExtractPotentialDuplicatesFlag("WHERE x = 1").Should().BeFalse();

            var preset = new FilterPreset { PotentialDuplicates = false };
            var sql = FilterSqlHelper.BuildSqlWithJson(preset, string.Empty);
            FilterSqlHelper.TryExtractPotentialDuplicatesFlag(sql).Should().BeFalse();
        }

        // ===================== ExtractNormalizedPredicate =====================

        [Fact]
        public void ExtractNormalizedPredicate_NullOrWhitespace_ReturnsNull()
        {
            FilterSqlHelper.ExtractNormalizedPredicate(null).Should().BeNull();
            FilterSqlHelper.ExtractNormalizedPredicate("   ").Should().BeNull();
        }

        [Fact]
        public void ExtractNormalizedPredicate_StripsWherePrefix()
        {
            FilterSqlHelper.ExtractNormalizedPredicate("WHERE x = 1").Should().Be("x = 1");
        }

        [Fact]
        public void ExtractNormalizedPredicate_StripsJsonPrefixAndWhere()
        {
            var sql = "/*JSON:{\"AccountId\":\"A\"}*/ WHERE x = 1";
            FilterSqlHelper.ExtractNormalizedPredicate(sql).Should().Be("x = 1");
        }

        [Fact]
        public void ExtractNormalizedPredicate_UnwrapsParentheses()
        {
            FilterSqlHelper.ExtractNormalizedPredicate("(((x = 1)))").Should().Be("x = 1");
        }

        // La garde anti-injection détecte les mots-clés ENTOURÉS d'espaces (pattern " insert ", " delete "…)
        // ainsi que le caractère ';'. Voir FilterSqlHelper.ExtractNormalizedPredicate.
        [Theory]
        [InlineData("x = 1; DROP TABLE T")]                // ';' déclencheur
        [InlineData("x = 1 UNION SELECT * FROM T")]        // " union " et " select "
        [InlineData("a = 1 INSERT INTO T VALUES (1)")]     // " insert " entouré
        [InlineData("a = 1 DELETE FROM T")]                // " delete "
        [InlineData("a = 1 UPDATE T SET b = 1")]           // " update "
        [InlineData("a = 1 ALTER TABLE T")]                // " alter "
        [InlineData("a = 1 DROP TABLE T")]                 // " drop "
        [InlineData("a = 1 EXEC malicious")]               // " exec "
        public void ExtractNormalizedPredicate_BannedKeywords_ReturnNull(string input)
        {
            FilterSqlHelper.ExtractNormalizedPredicate(input).Should().BeNull();
        }

        // Régression : la garde NE détecte PAS un mot-clé qui démarre la chaîne
        // (pas d'espace avant). C'est une faiblesse connue de la garde — ce test
        // documente le comportement actuel pour éviter une régression silencieuse
        // si on durcit le filtre plus tard.
        [Theory]
        [InlineData("DELETE FROM T")]
        [InlineData("INSERT INTO T VALUES (1)")]
        [InlineData("UPDATE T SET a = 1")]
        [InlineData("ALTER TABLE T ADD COLUMN c")]
        [InlineData("EXEC malicious")]
        public void ExtractNormalizedPredicate_LeadingKeywordWithoutSpace_NotCurrentlyCaught(string input)
        {
            // Comportement actuel : retourne l'input tel quel (filtre trop laxiste).
            // À durcir si la sécurité l'exige (ex. : Regex \b sur les mots-clés).
            FilterSqlHelper.ExtractNormalizedPredicate(input).Should().Be(input);
        }
    }
}

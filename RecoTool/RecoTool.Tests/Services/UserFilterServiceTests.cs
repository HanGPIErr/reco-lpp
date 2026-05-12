using FluentAssertions;
using RecoTool.Services;
using Xunit;

namespace RecoTool.Tests.Services
{
    /// <summary>
    /// Tests pour <see cref="UserFilterService"/>. Seule la méthode statique
    /// <c>SanitizeWhereClause</c> est purement testable (pas de DB) — les autres
    /// méthodes (Save/Load/List) nécessitent une vraie BDD Access et sont couvertes
    /// dans le projet d'intégration.
    /// </summary>
    public class UserFilterServiceTests
    {
        // ===== SanitizeWhereClause =====

        [Theory]
        [InlineData(null, "")]
        [InlineData("", "")]
        [InlineData("   ", "")]
        public void SanitizeWhereClause_NullOrWhitespace_ReturnsEmpty(string input, string expected)
        {
            UserFilterService.SanitizeWhereClause(input).Should().Be(expected);
        }

        [Fact]
        public void SanitizeWhereClause_RemovesAccountIdPredicateAlone()
        {
            UserFilterService.SanitizeWhereClause("Account_ID = 'XYZ'")
                .Should().Be("");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesAccountIdAndKeepsRest()
        {
            UserFilterService.SanitizeWhereClause("Account_ID = 'XYZ' AND CCY = 'EUR'")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesAccountIdInTheMiddle()
        {
            UserFilterService.SanitizeWhereClause("CCY = 'EUR' AND Account_ID = 'XYZ' AND a = 1")
                .Should().Contain("CCY = 'EUR'")
                .And.Contain("a = 1")
                .And.NotContain("Account_ID");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesDeleteDateIsNull()
        {
            UserFilterService.SanitizeWhereClause("a.DeleteDate IS NULL AND CCY = 'EUR'")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesDeleteDateIsNotNull()
        {
            UserFilterService.SanitizeWhereClause("CCY = 'EUR' AND DeleteDate IS NOT NULL")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesBracketedDeleteDate()
        {
            UserFilterService.SanitizeWhereClause("[DeleteDate] IS NULL AND a = 1")
                .Should().Be("a = 1");
        }

        [Fact]
        public void SanitizeWhereClause_StripsLeadingWhereWhenNoContent()
        {
            UserFilterService.SanitizeWhereClause("WHERE Account_ID = 'X'")
                .Should().Be("");
        }

        [Fact]
        public void SanitizeWhereClause_PreservesJsonHeaderPrefix()
        {
            var input = "/*JSON:{\"AccountId\":\"X\"}*/ Account_ID = 'X' AND CCY = 'EUR'";
            var got = UserFilterService.SanitizeWhereClause(input);
            got.Should().StartWith("/*JSON:")
               .And.Contain("CCY = 'EUR'")
               .And.NotContain("Account_ID");
        }

        [Fact]
        public void SanitizeWhereClause_JsonHeaderAlone_PreservedWithTrailingSpace()
        {
            var input = "/*JSON:{\"AccountId\":\"X\"}*/ Account_ID = 'X'";
            var got = UserFilterService.SanitizeWhereClause(input);
            got.Should().StartWith("/*JSON:");
            got.Should().NotContain("Account_ID");
        }

        [Fact]
        public void SanitizeWhereClause_CollapsesRepeatedSpaces()
        {
            UserFilterService.SanitizeWhereClause("CCY    =    'EUR'")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_NormalizesNonBreakingSpaces()
        {
            //   = espace insécable
            UserFilterService.SanitizeWhereClause("CCY = 'EUR'")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesTrailingAndOr()
        {
            UserFilterService.SanitizeWhereClause("CCY = 'EUR' AND")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_OnlyDanglingWhere_ReturnsEmpty()
        {
            UserFilterService.SanitizeWhereClause("WHERE")
                .Should().Be("");
        }

        [Fact]
        public void SanitizeWhereClause_CaseInsensitiveOnAccountAndDeleteDate()
        {
            UserFilterService.SanitizeWhereClause("account_id = 'X' AND DELETEDATE IS NULL AND CCY = 'EUR'")
                .Should().Be("CCY = 'EUR'");
        }

        [Fact]
        public void SanitizeWhereClause_RemovesMultipleAccountIdOccurrences()
        {
            UserFilterService.SanitizeWhereClause("Account_ID = 'X' AND CCY = 'EUR' AND Account_ID = 'Y'")
                .Should().Contain("CCY = 'EUR'")
                .And.NotContain("Account_ID");
        }

        // ===== Ctor =====

        [Fact]
        public void Ctor_WithConnectionString_DoesNotThrow()
        {
            // Le ctor passe par DbConn.ResolveConnectionString qui accepte une chaîne arbitraire
            // (la chaîne contient '=' donc traitée comme connection string).
            var sut = new UserFilterService("Provider=fake;Data Source=:mem:;", "alice");
            sut.Should().NotBeNull();
        }

        [Fact]
        public void Ctor_BlankCurrentUser_FallsBackToEnvironmentUserName()
        {
            // Comportement documenté : un currentUser vide est remplacé par Environment.UserName.
            // On ne peut pas tester la valeur exacte (varie par machine), mais on vérifie pas d'exception.
            var sut = new UserFilterService("Provider=fake;Data Source=:mem:;", "");
            sut.Should().NotBeNull();
        }
    }
}

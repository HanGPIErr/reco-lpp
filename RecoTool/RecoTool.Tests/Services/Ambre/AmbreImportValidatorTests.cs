using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.Ambre;
using Xunit;

namespace RecoTool.Tests.Services.Ambre
{
    /// <summary>
    /// Tests pour <see cref="AmbreImportValidator"/>. Service quasi-pur (utilise
    /// <see cref="RecoTool.Helpers.ValidationHelper"/> pour la validation de fichiers,
    /// qui touche le filesystem mais pas la DB).
    /// </summary>
    public class AmbreImportValidatorTests : IDisposable
    {
        private readonly string _tmpDir;

        public AmbreImportValidatorTests()
        {
            _tmpDir = Path.Combine(Path.GetTempPath(), "AmbreValidator_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tmpDir);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, true); } catch { }
        }

        private static AmbreImportValidator MakeSut() => new AmbreImportValidator();
        private static Country MakeCountry() => new Country
        {
            CNT_Id = "FR",
            CNT_AmbrePivot = "PIVOT_FR",
            CNT_AmbreReceivable = "RECV_FR"
        };

        // ===== ValidateFiles =====

        [Fact]
        public void ValidateFiles_Null_AddsErrorAndReturnsEmpty()
        {
            var result = new ImportResult();
            var got = MakeSut().ValidateFiles(null, isMultiFile: false, result);
            got.Should().BeEmpty();
            result.Errors.Should().Contain(e => e.Contains("No files"));
        }

        [Fact]
        public void ValidateFiles_EmptyArray_AddsErrorAndReturnsEmpty()
        {
            var result = new ImportResult();
            var got = MakeSut().ValidateFiles(Array.Empty<string>(), false, result);
            got.Should().BeEmpty();
            result.Errors.Should().Contain(e => e.Contains("No files"));
        }

        [Fact]
        public void ValidateFiles_OnlyWhitespacePaths_TreatedAsEmpty()
        {
            var result = new ImportResult();
            var got = MakeSut().ValidateFiles(new[] { "", "  ", null }, false, result);
            got.Should().BeEmpty();
            result.Errors.Should().Contain(e => e.Contains("No files"));
        }

        [Fact]
        public void ValidateFiles_TakesAtMostTwoFiles()
        {
            // 3 fichiers avec extension .xlsx (sans existence → ValidateImportFile renverra une erreur)
            var paths = new[]
            {
                Path.Combine(_tmpDir, "a.xlsx"),
                Path.Combine(_tmpDir, "b.xlsx"),
                Path.Combine(_tmpDir, "c.xlsx")
            };
            var result = new ImportResult();
            var got = MakeSut().ValidateFiles(paths, isMultiFile: true, result);
            got.Should().HaveCount(2, "seuls les 2 premiers fichiers sont retenus");
        }

        [Fact]
        public void ValidateFiles_MultiFileMode_PrefixesErrorsWithFileName()
        {
            // Fichier qui n'existe pas → ValidationHelper retourne erreur
            var path = Path.Combine(_tmpDir, "missing.xlsx");
            var result = new ImportResult();
            MakeSut().ValidateFiles(new[] { path }, isMultiFile: true, result);
            result.Errors.Should().Contain(e => e.StartsWith("missing.xlsx:"));
        }

        // ===== ValidateRequiredAccounts =====

        [Fact]
        public void ValidateRequiredAccounts_BothPresent_ReturnsTrueAndNoErrors()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "PIVOT_FR" } },
                new Dictionary<string, object> { { "Account_ID", "RECV_FR" } }
            };
            var result = new ImportResult();
            MakeSut().ValidateRequiredAccounts(data, MakeCountry(), result).Should().BeTrue();
            result.Errors.Should().BeEmpty();
        }

        [Fact]
        public void ValidateRequiredAccounts_MissingPivot_ReturnsFalseAndError()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "RECV_FR" } }
            };
            var result = new ImportResult();
            MakeSut().ValidateRequiredAccounts(data, MakeCountry(), result).Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Pivot=PIVOT_FR"));
        }

        [Fact]
        public void ValidateRequiredAccounts_MissingReceivable_ReturnsFalseAndError()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "PIVOT_FR" } }
            };
            var result = new ImportResult();
            MakeSut().ValidateRequiredAccounts(data, MakeCountry(), result).Should().BeFalse();
            result.Errors.Should().Contain(e => e.Contains("Receivable=RECV_FR"));
        }

        [Fact]
        public void ValidateRequiredAccounts_BothMissing_ListsBothInError()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "OTHER" } }
            };
            var result = new ImportResult();
            MakeSut().ValidateRequiredAccounts(data, MakeCountry(), result).Should().BeFalse();
            result.Errors.Should().HaveCount(1);
            result.Errors[0].Should().Contain("Pivot=PIVOT_FR").And.Contain("Receivable=RECV_FR");
        }

        [Fact]
        public void ValidateRequiredAccounts_CaseInsensitiveMatch()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "pivot_fr" } },
                new Dictionary<string, object> { { "Account_ID", "RECV_FR" } }
            };
            MakeSut().ValidateRequiredAccounts(data, MakeCountry(), new ImportResult()).Should().BeTrue();
        }

        [Fact]
        public void ValidateRequiredAccounts_CountryWithNullPivot_ReturnsFalse()
        {
            var data = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { { "Account_ID", "ANY" } }
            };
            var result = new ImportResult();
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = null, CNT_AmbreReceivable = "R" };
            MakeSut().ValidateRequiredAccounts(data, country, result).Should().BeFalse();
        }

        // ===== ValidateTransformedData =====

        [Fact]
        public void ValidateTransformedData_AllValid_ReturnsAllInValidList()
        {
            // ValidateDataCoherence est actuellement vide (commentée) → tout passe.
            var data = new List<DataAmbre>
            {
                new DataAmbre { ID = "1", Account_ID = "X", SignedAmount = 100m, Event_Num = "E1" },
                new DataAmbre { ID = "2", Account_ID = "Y", SignedAmount = 50m, Event_Num = "E2" }
            };
            var (errors, valid) = MakeSut().ValidateTransformedData(data, MakeCountry());
            errors.Should().BeEmpty();
            valid.Should().HaveCount(2);
        }

        [Fact]
        public void ValidateTransformedData_EmptyInput_ReturnsEmpty()
        {
            var (errors, valid) = MakeSut().ValidateTransformedData(new List<DataAmbre>(), MakeCountry());
            errors.Should().BeEmpty();
            valid.Should().BeEmpty();
        }
    }
}

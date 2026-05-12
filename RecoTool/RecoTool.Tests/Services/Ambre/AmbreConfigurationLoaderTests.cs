using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using RecoTool.Models;
using RecoTool.Services;
using RecoTool.Services.Ambre;
using Xunit;

namespace RecoTool.Tests.Services.Ambre
{
    /// <summary>
    /// Tests pour <see cref="AmbreConfigurationLoader"/>. Désormais testable via Moq
    /// sur <see cref="IOfflineFirstService"/> (les 5 méthodes référentielles ont été
    /// ajoutées à l'interface).
    /// </summary>
    public class AmbreConfigurationLoaderTests
    {
        // ===== Ctor =====

        [Fact]
        public void Ctor_NullDependency_Throws()
        {
            Action a = () => new AmbreConfigurationLoader(null);
            a.Should().Throw<ArgumentNullException>().WithMessage("*offlineFirstService*");
        }

        // ===== EnsureInitializedAsync =====

        [Fact]
        public async Task EnsureInitializedAsync_FirstCall_LoadsCountriesAndCodes()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>
            {
                new Country { CNT_Id = "FR", CNT_AmbrePivot = "P_FR", CNT_AmbreReceivable = "R_FR" }
            });
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns(new List<AmbreTransactionCode>
            {
                new AmbreTransactionCode { ATC_CODE = "PAY", ATC_TAG = "PAYMENT" },
                new AmbreTransactionCode { ATC_CODE = "COL", ATC_TAG = "COLLECTION" }
            });

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();

            sut.TransformationService.Should().NotBeNull();
            sut.CodeToCategory.Should().NotBeNull();
            sut.CodeToCategory.Should().ContainKey("PAY")
                .WhoseValue.Should().Be(TransactionType.PAYMENT);
            sut.CodeToCategory.Should().ContainKey("COL")
                .WhoseValue.Should().Be(TransactionType.COLLECTION);
        }

        [Fact]
        public async Task EnsureInitializedAsync_SecondCall_DoesNotReloadCountries()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns(new List<AmbreTransactionCode>());

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();
            await sut.EnsureInitializedAsync();

            ofs.Verify(x => x.GetCountries(), Times.Once);
            ofs.Verify(x => x.GetAmbreTransactionCodes(), Times.Once);
        }

        [Fact]
        public async Task EnsureInitializedAsync_NullTransactionCodes_HandledGracefully()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns((List<AmbreTransactionCode>)null);

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();

            sut.CodeToCategory.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public async Task EnsureInitializedAsync_UnknownTagIsSkipped()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns(new List<AmbreTransactionCode>
            {
                new AmbreTransactionCode { ATC_CODE = "BAD", ATC_TAG = "NOT_AN_ENUM_VALUE" },
                new AmbreTransactionCode { ATC_CODE = "OK", ATC_TAG = "PAYMENT" }
            });

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();

            sut.CodeToCategory.Should().NotContainKey("BAD");
            sut.CodeToCategory.Should().ContainKey("OK");
        }

        [Fact]
        public async Task EnsureInitializedAsync_TagNormalizesSpacesAndDashes()
        {
            // Le code remplace " " et "-" par "_" avant Enum.TryParse pour gérer
            // par exemple "TO CATEGORIZE" → "TO_CATEGORIZE"
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns(new List<AmbreTransactionCode>
            {
                new AmbreTransactionCode { ATC_CODE = "TC", ATC_TAG = "TO CATEGORIZE" }
            });

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();

            sut.CodeToCategory.Should().ContainKey("TC")
                .WhoseValue.Should().Be(TransactionType.TO_CATEGORIZE);
        }

        [Fact]
        public async Task EnsureInitializedAsync_EmptyCodeOrTag_Skipped()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountries()).ReturnsAsync(new List<Country>());
            ofs.Setup(x => x.GetAmbreTransactionCodes()).Returns(new List<AmbreTransactionCode>
            {
                new AmbreTransactionCode { ATC_CODE = "", ATC_TAG = "PAYMENT" },
                new AmbreTransactionCode { ATC_CODE = "X", ATC_TAG = "" },
                new AmbreTransactionCode { ATC_CODE = null, ATC_TAG = null },
                new AmbreTransactionCode { ATC_CODE = "VALID", ATC_TAG = "PAYMENT" }
            });

            var sut = new AmbreConfigurationLoader(ofs.Object);
            await sut.EnsureInitializedAsync();

            sut.CodeToCategory.Should().HaveCount(1);
            sut.CodeToCategory.Should().ContainKey("VALID");
        }

        // ===== LoadConfigurationsAsync =====

        [Fact]
        public async Task LoadConfigurationsAsync_HappyPath_ReturnsConfiguration()
        {
            var country = new Country { CNT_Id = "FR", CNT_AmbrePivot = "P_FR" };
            var fields = new List<AmbreImportField> { new AmbreImportField { AMB_Source = "A", AMB_Destination = "B" } };
            var transforms = new List<AmbreTransform> { new AmbreTransform { AMB_Source = "X", AMB_Destination = "Y" } };

            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountryByIdAsync("FR")).ReturnsAsync(country);
            ofs.Setup(x => x.GetAmbreImportFields()).Returns(fields);
            ofs.Setup(x => x.GetAmbreTransforms()).Returns(transforms);

            var sut = new AmbreConfigurationLoader(ofs.Object);
            var result = new ImportResult();
            var config = await sut.LoadConfigurationsAsync("FR", result);

            config.Should().NotBeNull();
            config.Country.CNT_Id.Should().Be("FR");
            config.ImportFields.Should().HaveCount(1);
            config.Transforms.Should().HaveCount(1);
            result.Errors.Should().BeEmpty();
        }

        [Fact]
        public async Task LoadConfigurationsAsync_CountryNotFound_AddsErrorAndReturnsNull()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountryByIdAsync("ZZ")).ReturnsAsync((Country)null);
            ofs.Setup(x => x.GetAmbreImportFields()).Returns(new List<AmbreImportField>());
            ofs.Setup(x => x.GetAmbreTransforms()).Returns(new List<AmbreTransform>());

            var sut = new AmbreConfigurationLoader(ofs.Object);
            var result = new ImportResult();
            var config = await sut.LoadConfigurationsAsync("ZZ", result);

            config.Should().BeNull();
            result.Errors.Should().Contain(e => e.Contains("not found") && e.Contains("ZZ"));
        }

        [Fact]
        public async Task LoadConfigurationsAsync_ExceptionInLoader_CaughtAndLoggedToResult()
        {
            var ofs = new Mock<IOfflineFirstService>();
            ofs.Setup(x => x.GetCountryByIdAsync("FR"))
                .ThrowsAsync(new InvalidOperationException("DB unreachable"));

            var sut = new AmbreConfigurationLoader(ofs.Object);
            var result = new ImportResult();
            var config = await sut.LoadConfigurationsAsync("FR", result);

            config.Should().BeNull();
            result.Errors.Should().Contain(e => e.Contains("Error loading configuration"));
        }
    }
}

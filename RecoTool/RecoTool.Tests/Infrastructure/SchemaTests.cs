using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using RecoTool.Infrastructure;
using Xunit;

namespace RecoTool.Tests.Infrastructure
{
    /// <summary>
    /// Sanity checks for <see cref="Schema"/> — the centralized table/column constants
    /// referenced from hand-written SQL across the codebase.
    ///
    /// <para>
    /// These tests are intentionally cheap: they verify that the constants exist, are
    /// non-empty, do not contain placeholder/typo characters (e.g. unmatched brackets,
    /// stray whitespace, lingering <c>TODO</c> markers), and that a representative
    /// sample matches the canonical schema produced by <c>DatabaseRecreationService</c>.
    /// </para>
    /// </summary>
    public class SchemaTests
    {
        [Fact]
        public void Tables_KnownConstants_MatchExpectedValues()
        {
            Schema.Tables.T_Reconciliation.Should().Be("T_Reconciliation");
            Schema.Tables.T_Data_Ambre.Should().Be("T_Data_Ambre");
            Schema.Tables.T_Ref_User_Filter.Should().Be("T_Ref_User_Filter");
            Schema.Tables.T_Param.Should().Be("T_Param");
            Schema.Tables.T_DW_Data.Should().Be("T_DW_Data");
            Schema.Tables.T_DW_Guarantee.Should().Be("T_DW_Guarantee");
            Schema.Tables.T_Reco_Rules.Should().Be("T_Reco_Rules");
            Schema.Tables.T_RuleProposals.Should().Be("T_RuleProposals");
            Schema.Tables.T_SyncChangeLog.Should().Be("T_SyncChangeLog");
            Schema.Tables.T_User.Should().Be("T_User");
        }

        [Fact]
        public void Columns_Reconciliation_KnownConstants_MatchExpectedValues()
        {
            Schema.Columns.Reconciliation.ID.Should().Be("ID");
            Schema.Columns.Reconciliation.DWINGS_InvoiceID.Should().Be("DWINGS_InvoiceID");
            Schema.Columns.Reconciliation.DWINGS_GuaranteeID.Should().Be("DWINGS_GuaranteeID");
            Schema.Columns.Reconciliation.DWINGS_BGPMT.Should().Be("DWINGS_BGPMT");
            Schema.Columns.Reconciliation.Action.Should().Be("Action");
            Schema.Columns.Reconciliation.LastModified.Should().Be("LastModified");
        }

        [Fact]
        public void Columns_Ambre_KnownConstants_MatchExpectedValues()
        {
            Schema.Columns.Ambre.ID.Should().Be("ID");
            Schema.Columns.Ambre.SignedAmount.Should().Be("SignedAmount");
            Schema.Columns.Ambre.LocalSignedAmount.Should().Be("LocalSignedAmount");
            Schema.Columns.Ambre.Operation_Date.Should().Be("Operation_Date");
            Schema.Columns.Ambre.Value_Date.Should().Be("Value_Date");
            Schema.Columns.Ambre.Country.Should().Be("Country");
        }

        [Fact]
        public void Columns_UserFilter_KnownConstants_MatchExpectedValues()
        {
            Schema.Columns.UserFilter.UFI_id.Should().Be("UFI_id");
            Schema.Columns.UserFilter.UFI_Name.Should().Be("UFI_Name");
            Schema.Columns.UserFilter.UFI_SQL.Should().Be("UFI_SQL");
            Schema.Columns.UserFilter.UFI_CreatedBy.Should().Be("UFI_CreatedBy");
        }

        [Fact]
        public void Columns_TodoList_KnownConstants_MatchExpectedValues()
        {
            Schema.Columns.TodoList.TDL_id.Should().Be("TDL_id");
            Schema.Columns.TodoList.TDL_Name.Should().Be("TDL_Name");
            Schema.Columns.TodoList.TDL_FilterName.Should().Be("TDL_FilterName");
            Schema.Columns.TodoList.TDL_ViewName.Should().Be("TDL_ViewName");
        }

        [Fact]
        public void Columns_DwingsData_KnownConstants_MatchExpectedValues()
        {
            Schema.Columns.DwingsData.BGPMT.Should().Be("BGPMT");
            Schema.Columns.DwingsData.INVOICE_ID.Should().Be("INVOICE_ID");
            Schema.Columns.DwingsData.T_INVOICE_STATUS.Should().Be("T_INVOICE_STATUS");
            Schema.Columns.DwingsData.T_PAYMENT_REQUEST_STATUS.Should().Be("T_PAYMENT_REQUEST_STATUS");
        }

        [Fact]
        public void AllConstants_AreNonEmptyAndCleanIdentifiers()
        {
            // Walk every nested static class and verify every public const string
            // is non-null, non-whitespace, and looks like a plausible SQL identifier
            // (no brackets, no quotes, no whitespace, no TODO/FIXME markers).
            var values = EnumerateStringConstants(typeof(Schema)).ToList();

            values.Should().NotBeEmpty("Schema should expose at least one constant");

            foreach (var (path, value) in values)
            {
                value.Should().NotBeNullOrWhiteSpace($"constant {path} must have a value");
                value.Should().NotContain(" ", $"constant {path} must not contain whitespace");
                value.Should().NotContain("[", $"constant {path} must not include OleDb brackets");
                value.Should().NotContain("]", $"constant {path} must not include OleDb brackets");
                value.Should().NotContain("\"", $"constant {path} must not include quote chars");
                value.Should().NotContain("'", $"constant {path} must not include quote chars");
                value.Should().NotContain("TODO", $"constant {path} must not contain a TODO placeholder");
                value.Should().NotContain("FIXME", $"constant {path} must not contain a FIXME placeholder");
                value.Should().NotContain("?", $"constant {path} must not contain a parameter placeholder");
            }
        }

        [Fact]
        public void AllConstants_AreUniqueWithinTheirOwningClass()
        {
            // It's legal for two tables to share a column name (e.g. ID), but within
            // a single inner class every constant must map to a unique value — a
            // duplicate would almost certainly be a copy-paste typo.
            foreach (var inner in typeof(Schema.Columns).GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
            {
                var values = inner
                    .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                    .Where(f => f.IsLiteral && f.FieldType == typeof(string))
                    .Select(f => (string)f.GetRawConstantValue())
                    .ToList();

                values.Should().OnlyHaveUniqueItems($"every column constant in {inner.Name} must be unique");
            }
        }

        private static IEnumerable<(string path, string value)> EnumerateStringConstants(Type root)
        {
            // Direct const string fields on root
            foreach (var field in root.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (field.IsLiteral && field.FieldType == typeof(string))
                {
                    yield return ($"{root.FullName}.{field.Name}", (string)field.GetRawConstantValue());
                }
            }

            // Recurse into nested static classes
            foreach (var nested in root.GetNestedTypes(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (var item in EnumerateStringConstants(nested))
                {
                    yield return item;
                }
            }
        }
    }
}

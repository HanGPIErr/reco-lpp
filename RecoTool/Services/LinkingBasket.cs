using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using RecoTool.Services.DTOs;

namespace RecoTool.Services
{
    /// <summary>
    /// Cross-view basket that holds Pivot and Receivable lines selected for linking.
    /// Lives at the ReconciliationPage level, persists across tab switches.
    /// </summary>
    public class LinkingBasket : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public ObservableCollection<LinkingBasketItem> PivotItems { get; } = new ObservableCollection<LinkingBasketItem>();
        public ObservableCollection<LinkingBasketItem> ReceivableItems { get; } = new ObservableCollection<LinkingBasketItem>();

        public LinkingBasket()
        {
            PivotItems.CollectionChanged += (_, __) => RefreshSummary();
            ReceivableItems.CollectionChanged += (_, __) => RefreshSummary();
        }

        public int TotalCount => PivotItems.Count + ReceivableItems.Count;
        public bool HasItems => TotalCount > 0;
        public bool CanLink => PivotItems.Count > 0 && ReceivableItems.Count > 0;

        public decimal PivotTotal => PivotItems.Sum(x => x.Amount);
        public decimal ReceivableTotal => ReceivableItems.Sum(x => x.Amount);
        public decimal Delta => PivotTotal + ReceivableTotal;
        public bool IsBalanced => Math.Abs(Delta) < 0.01m;

        public string Summary
        {
            get
            {
                if (!HasItems) return string.Empty;
                var pCount = PivotItems.Count;
                var rCount = ReceivableItems.Count;
                var delta = Delta;
                var status = IsBalanced ? "✅ Balanced" : $"⚠️ Δ {delta:N2}";
                return $"P: {pCount} ({PivotTotal:N2}) | R: {rCount} ({ReceivableTotal:N2}) | {status}";
            }
        }

        public void AddPivot(ReconciliationViewData row)
        {
            if (row == null || PivotItems.Any(x => x.Id == row.ID)) return;
            PivotItems.Add(LinkingBasketItem.FromRow(row, "P"));
        }

        public void AddReceivable(ReconciliationViewData row)
        {
            if (row == null || ReceivableItems.Any(x => x.Id == row.ID)) return;
            ReceivableItems.Add(LinkingBasketItem.FromRow(row, "R"));
        }

        public void AddRow(ReconciliationViewData row, string accountSide)
        {
            if (string.Equals(accountSide, "P", StringComparison.OrdinalIgnoreCase))
                AddPivot(row);
            else if (string.Equals(accountSide, "R", StringComparison.OrdinalIgnoreCase))
                AddReceivable(row);
        }

        public void RemoveItem(string id)
        {
            var p = PivotItems.FirstOrDefault(x => x.Id == id);
            if (p != null) { PivotItems.Remove(p); return; }
            var r = ReceivableItems.FirstOrDefault(x => x.Id == id);
            if (r != null) ReceivableItems.Remove(r);
        }

        public void Clear()
        {
            PivotItems.Clear();
            ReceivableItems.Clear();
        }

        /// <summary>
        /// Returns all IDs in the basket (Pivot + Receivable).
        /// </summary>
        public List<string> GetAllIds()
        {
            return PivotItems.Select(x => x.Id)
                .Concat(ReceivableItems.Select(x => x.Id))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private void RefreshSummary()
        {
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(CanLink));
            OnPropertyChanged(nameof(PivotTotal));
            OnPropertyChanged(nameof(ReceivableTotal));
            OnPropertyChanged(nameof(Delta));
            OnPropertyChanged(nameof(IsBalanced));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public class LinkingBasketItem
    {
        public string Id { get; set; }
        public string Side { get; set; }
        public decimal Amount { get; set; }
        public string Currency { get; set; }
        public string Reference { get; set; }
        public DateTime? OperationDate { get; set; }

        public string Display => $"{Reference ?? Id}  {Amount:N2} {Currency}";

        public static LinkingBasketItem FromRow(ReconciliationViewData row, string side)
        {
            return new LinkingBasketItem
            {
                Id = row.ID,
                Side = side,
                Amount = row.SignedAmount,
                Currency = row.CCY,
                Reference = row.Reconciliation_Num ?? row.Event_Num ?? row.ID,
                OperationDate = row.Operation_Date
            };
        }
    }
}

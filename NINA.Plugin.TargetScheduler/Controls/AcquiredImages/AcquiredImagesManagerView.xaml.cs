using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NINA.Plugin.TargetScheduler.Controls.AcquiredImages {

    public partial class AcquiredImagesManagerView : UserControl {

        public AcquiredImagesManagerView() {
            InitializeComponent();
        }

        private void SelectionCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
            if (sender is CheckBox checkBox) {
                checkBox.Focus();
                checkBox.IsChecked = !checkBox.IsChecked.GetValueOrDefault();
                e.Handled = true;
            }
        }

        private void columnHeader_Click(object sender, RoutedEventArgs e) {
            var columnHeader = sender as DataGridColumnHeader;
            if (columnHeader != null) {
                string propertyName = GetPropertyName(GetColumnTitle(columnHeader));
                AcquiredImagesManagerViewVM vm = this.DataContext as AcquiredImagesManagerViewVM;
                SortDescription sortDescription = GetSortDescription(vm.ItemsView.SortDescriptions, propertyName);
                vm.ItemsView.SortDescriptions.Clear();
                vm.ItemsView.SortDescriptions.Add(sortDescription);
            }
        }

        private SortDescription GetSortDescription(SortDescriptionCollection sortDescriptions, string propertyName) {
            if (propertyName == null) {
                return new SortDescription("AcquiredDate", ListSortDirection.Descending);
            }

            ListSortDirection sortDirection = ListSortDirection.Ascending;
            SortDescription current = sortDescriptions[0];
            if (current.PropertyName == propertyName) {
                sortDirection = current.Direction == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            }

            return new SortDescription(propertyName, sortDirection);
        }

        private string GetPropertyName(string title) {
            switch (title) {
                case "Date": return "AcquiredDate";
                case "Project": return "ProjectName";
                case "Target": return "TargetName";
                case "Filter": return "FilterName";
                case "Stars": return "DetectedStars";
                case "HFR": return "HFR";
                case "FWHM": return "FWHM";
                case "Eccentricity": return "Eccentricity";
                case "Accepted": return "Accepted";
                case "Reject Reason": return "RejectReason";
                default: return null;
            }
        }

        private string GetColumnTitle(DataGridColumnHeader columnHeader) {
            TextBlock textBlock = columnHeader?.Content as TextBlock;
            return (textBlock != null) ? textBlock.Text : null;
        }
    }
}
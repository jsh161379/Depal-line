using System.Windows.Controls;

namespace BareunOrder.Feature.Certificate;

public partial class CertificateView : UserControl {
    public CertificateView() {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is CertificateViewModel oldVm)
                oldVm.ReportBundleChanged -= RefreshReportPreview;
            if (e.NewValue is CertificateViewModel newVm)
                newVm.ReportBundleChanged += RefreshReportPreview;

            RefreshReportPreview();
        };
    }

    private void RefreshReportPreview() {
        if (DataContext is not CertificateViewModel vm || vm.ReportBundle == null) {
            ReportPreviewViewer.Document = null;
            return;
        }
        if (!vm.IsReportTab) return;

        var doc = OrderPeriodReportPrinter.BuildDocument(vm.ReportBundle, vm.OperatorName);
        doc.PageWidth = double.NaN;
        doc.ColumnWidth = double.NaN;
        ReportPreviewViewer.Document = doc;
    }
}
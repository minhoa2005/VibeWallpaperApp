namespace VibeWallpaper.App.Services;

public sealed record UserErrorPresentation(
    string Title,
    string Summary,
    string Cause,
    string SuggestedAction,
    string DiagnosticCode)
{
    public string DetailedMessage =>
        $"{Summary}\nNguyên nhân: {Cause}\nCách xử lý: {SuggestedAction}\nMã chẩn đoán: {DiagnosticCode}";
}

public static class UserErrorPresenter
{
    public static UserErrorPresentation Create(string? code, string? summary = null)
    {
        var diagnosticCode = string.IsNullOrWhiteSpace(code) ? "application.operation.failed" : code;
        var (title, fallbackSummary, cause, action) = diagnosticCode switch
        {
            "video.source.unsupported" => (
                "Định dạng video không được hỗ trợ",
                "Không thể thêm video này.",
                "Phần mở rộng của tệp không thuộc các định dạng MP4, WebM, MKV, MOV hoặc GIF.",
                "Chuyển video sang một định dạng được hỗ trợ rồi thử lại."),
            "video.source.missing" => (
                "Không tìm thấy video",
                "Không thể đọc tệp video đã chọn.",
                "Tệp đã bị di chuyển, đổi tên, xóa hoặc ổ đĩa hiện không khả dụng.",
                "Kiểm tra đường dẫn và chọn lại tệp đang tồn tại."),
            "video.probe.invalid" => (
                "Video không thể phát",
                "Không thể xác nhận video là nội dung có thể phát.",
                "Tệp có thể bị hỏng, thiếu track hình ảnh hoặc dùng codec mà runtime không giải mã được.",
                "Thử phát tệp bằng trình phát khác hoặc chuyển mã sang H.264 MP4."),
            "video.helper.timeout" => (
                "Kiểm tra video quá thời gian",
                "Thành phần đọc thông tin video không hoàn tất đúng hạn.",
                "Lần khởi động bộ giải mã đầu tiên, ổ đĩa chậm hoặc tệp bất thường có thể làm thao tác kéo dài.",
                "Thử lại một lần; nếu vẫn lỗi, chuyển video sang H.264 MP4 hoặc kiểm tra ổ đĩa."),
            "video.runtime.unavailable" => (
                "Thiếu thành phần xử lý video",
                "Ứng dụng không thể khởi động runtime kiểm tra video.",
                "Payload MediaProbe hoặc thư viện LibVLC bị thiếu, không đầy đủ hoặc không tương thích x64.",
                "Cài lại bản ứng dụng đầy đủ hoặc kiểm tra các tệp MediaProbe và thư mục libvlc."),
            "video.helper.crashed" or "video.helper.invalid_response" => (
                "Thành phần kiểm tra video gặp lỗi",
                "Ứng dụng không nhận được kết quả kiểm tra video hợp lệ.",
                "Tiến trình MediaProbe đã dừng đột ngột hoặc trả về dữ liệu không đọc được.",
                "Khởi động lại ứng dụng; nếu lỗi lặp lại, kiểm tra log và cài lại bản đầy đủ."),
            "video.source.changed_during_import" => (
                "Video thay đổi trong lúc nhập",
                "Ứng dụng đã dừng nhập để tránh lưu metadata không còn đúng.",
                "Một chương trình khác đang ghi, đồng bộ hoặc thay thế tệp video.",
                "Chờ thao tác với tệp hoàn tất rồi thêm lại."),
            "library.item.duplicate" => (
                "Wallpaper đã tồn tại",
                "Nguồn này đã có trong thư viện.",
                "Ứng dụng ngăn tạo hai mục trỏ tới cùng một nguồn.",
                "Dùng mục đã có hoặc chọn một nguồn khác."),
            "wallpaper.output.group_count" => (
                "Chưa chọn đủ màn hình",
                "Không thể áp dụng chế độ nhóm.",
                "Duplicate và Span cần ít nhất hai màn hình được chọn.",
                "Tích chọn ít nhất hai màn hình rồi thử lại."),
            "wallpaper.output.unavailable" or "wallpaper.host.unavailable" => (
                "Màn hình không còn khả dụng",
                "Không thể đặt wallpaper lên màn hình đã chọn.",
                "Cấu hình màn hình hoặc desktop host đã thay đổi kể từ lúc giao diện được tải.",
                "Mở lại trang Màn hình hoặc kết nối lại màn hình rồi thử lại."),
            "wallpaper.engine.unavailable" => (
                "Wallpaper engine chưa sẵn sàng",
                "Không thể thực hiện lệnh đặt wallpaper.",
                "Engine nền chưa khởi tạo xong hoặc đã dừng.",
                "Khởi động lại ứng dụng và thử lại."),
            _ => (
                "Không thể hoàn tất thao tác",
                "Ứng dụng gặp lỗi khi xử lý yêu cầu.",
                "Lỗi nội bộ hoặc lỗi lưu trạng thái không được phân loại cụ thể.",
                "Thử lại; nếu lỗi lặp lại, dùng mã chẩn đoán để kiểm tra log."),
        };

        return new UserErrorPresentation(
            title,
            string.IsNullOrWhiteSpace(summary) ? fallbackSummary : summary,
            cause,
            action,
            diagnosticCode);
    }
}

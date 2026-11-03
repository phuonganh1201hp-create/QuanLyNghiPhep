using System.Text.Json;
using QuanLyNghiPhepHV.Models;

namespace QuanLyNghiPhepHV.Services;

// Kho dữ liệu dùng chung giữa các phiên; ghi JSON để không mất dữ liệu khi chạy lại.
// Mỗi thao tác ghi đều kiểm tra quyền ở tầng xử lý, không chỉ ẩn nút trên giao diện.
public sealed class LeaveRepository
{
    private readonly object _gate = new();
    private readonly string _file;
    private readonly RepositoryData _data;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LeaveRepository(IWebHostEnvironment env)
    {
        string dir = Path.Combine(env.ContentRootPath, "AppData");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "du-lieu-nghi-phep.json");
        if (File.Exists(_file))
        {
            _data = JsonSerializer.Deserialize<RepositoryData>(File.ReadAllText(_file))
                ?? throw new InvalidDataException("Tệp dữ liệu không hợp lệ.");
            // Đơn cũ chưa có duyệt hai cấp không được coi là đã hoàn tất.
            bool migrated = false;
            foreach (var x in _data.Requests)
            {
                if (x.TrangThai is "Chờ duyệt" or "Đã duyệt")
                {
                    x.TrangThai = LeaveStatuses.WaitingTrungDoi;
                    x.NguoiDuyet = "";
                    x.NgayDuyet = null;
                    migrated = true;
                }
            }
            if (migrated) SaveUnsafe();
        }
        else
        {
            _data = SeedData();
            SaveUnsafe();
        }
    }

    public List<Student> GetStudents(DemoSession session)
    {
        RequireLoggedIn(session);
        lock (_gate) return _data.Students.Select(x => new Student
        {
            MaHocVien = x.MaHocVien,
            HoTen = x.HoTen,
            Lop = x.Lop,
            TrungDoi = x.TrungDoi
        }).ToList();
    }

    public List<LeaveRequest> GetRequests(DemoSession session)
    {
        RequireLoggedIn(session);
        lock (_gate) return _data.Requests
            .Where(x => session.Role switch
            {
                UserRole.PhongQLHV => x.NgayDuyetTrungDoi.HasValue
                    && (x.TrangThai == LeaveStatuses.WaitingPhong || x.NgayDuyetPhong.HasValue),
                UserRole.BoMon => x.TrangThai == LeaveStatuses.Approved
                    && x.NgayDuyetTrungDoi.HasValue && x.NgayDuyetPhong.HasValue,
                _ => true
            })
            .OrderByDescending(x => x.NgayTao)
            .ThenByDescending(x => x.Id)
            .Select(x => x.Clone()).ToList();
    }

    public List<AuditEntry> GetAudit(DemoSession session)
    {
        RequireLoggedIn(session);
        if (session.Role == UserRole.BoMon)
            throw new UnauthorizedAccessException("Giáo viên bộ môn không có quyền xem nhật ký hệ thống.");
        lock (_gate) return _data.Audit.OrderByDescending(x => x.At)
            .Take(100).Select(x => new AuditEntry
            {
                At = x.At, User = x.User, Action = x.Action, Detail = x.Detail
            }).ToList();
    }

    public LeaveRequest Add(DemoSession session, LeaveInput input)
    {
        RequireRole(session, UserRole.TrungDoi);
        lock (_gate)
        {
            var student = ValidateInput(input);
            var id = _data.NextId++;
            var request = new LeaveRequest
            {
                Id = id,
                MaDon = $"NP{DateTime.Today:yyyy}-{id:0000}",
                MaHocVien = student.MaHocVien,
                HoTen = student.HoTen,
                Lop = student.Lop,
                TrungDoi = input.TrungDoi.Trim(),
                TuThoiDiem = input.TuThoiDiem,
                DenThoiDiem = input.DenThoiDiem,
                LyDo = input.LyDo.Trim(),
                DiaChiNoiNghi = input.DiaChiNoiNghi.Trim(),
                TrangThai = LeaveStatuses.WaitingTrungDoi,
                NgayTao = DateTime.Now,
                NguoiTao = session.UserName
            };
            RegisterManualStudentUnsafe(input, student);
            _data.Requests.Add(request);
            LogUnsafe(session, "Tạo đơn", $"{request.HoTen} ({request.MaHocVien})");
            SaveUnsafe();
            return request.Clone();
        }
    }

    public LeaveRequest Update(DemoSession session, int id, LeaveInput input)
    {
        RequireRole(session, UserRole.TrungDoi);
        lock (_gate)
        {
            var request = FindUnsafe(id);
            RequirePending(request);
            var student = ValidateInput(input);
            RegisterManualStudentUnsafe(input, student);
            request.MaHocVien = student.MaHocVien;
            request.HoTen = student.HoTen;
            request.Lop = student.Lop;
            request.TrungDoi = input.TrungDoi.Trim();
            request.TuThoiDiem = input.TuThoiDiem;
            request.DenThoiDiem = input.DenThoiDiem;
            request.LyDo = input.LyDo.Trim();
            request.DiaChiNoiNghi = input.DiaChiNoiNghi.Trim();
            LogUnsafe(session, "Sửa đơn", $"{request.HoTen} ({request.MaHocVien})");
            SaveUnsafe();
            return request.Clone();
        }
    }

    public void Delete(DemoSession session, int id)
    {
        RequireRole(session, UserRole.TrungDoi);
        lock (_gate)
        {
            var request = FindUnsafe(id);
            RequirePending(request);
            _data.Requests.Remove(request);
            LogUnsafe(session, "Xóa đơn", $"{request.HoTen} ({request.MaHocVien})");
            SaveUnsafe();
        }
    }

    public LeaveRequest Review(DemoSession session, int id, bool approved, string note)
    {
        RequireLoggedIn(session);
        if (session.Role is not (UserRole.TrungDoi or UserRole.PhongQLHV))
            throw new UnauthorizedAccessException("Vai trò này không có quyền xét duyệt.");
        lock (_gate)
        {
            var request = FindUnsafe(id);
            bool isTrungDoi = session.Role == UserRole.TrungDoi;
            string required = isTrungDoi ? LeaveStatuses.WaitingTrungDoi : LeaveStatuses.WaitingPhong;
            if (request.TrangThai != required)
                throw new InvalidOperationException(isTrungDoi
                    ? "Đơn đã qua bước xét duyệt trung đội."
                    : "Đơn chưa được trung đội duyệt hoặc đã được xử lý.");
            if (!approved && string.IsNullOrWhiteSpace(note))
                throw new ArgumentException("Cần ghi lý do khi từ chối đơn.");
            if (note.Length > 300)
                throw new ArgumentException("Ghi chú xét duyệt không vượt quá 300 ký tự.");
            var now = DateTime.Now;
            if (isTrungDoi)
            {
                request.NguoiDuyetTrungDoi = session.UserName;
                request.NgayDuyetTrungDoi = now;
                request.TrangThai = approved ? LeaveStatuses.WaitingPhong : LeaveStatuses.Rejected;
            }
            else
            {
                if (!request.NgayDuyetTrungDoi.HasValue)
                    throw new InvalidOperationException("Thiếu xác nhận của giáo viên trung đội.");
                request.NguoiDuyetPhong = session.UserName;
                request.NgayDuyetPhong = now;
                request.TrangThai = approved ? LeaveStatuses.Approved : LeaveStatuses.Rejected;
            }
            request.NguoiDuyet = session.UserName;
            request.NgayDuyet = now;
            request.GhiChuDuyet = note.Trim();
            LogUnsafe(session, isTrungDoi
                ? (approved ? "Trung đội duyệt" : "Trung đội từ chối")
                : (approved ? "Phòng QLHV duyệt" : "Phòng QLHV từ chối"),
                $"{request.HoTen} ({request.MaHocVien})");
            SaveUnsafe();
            return request.Clone();
        }
    }

    public Student AddStudent(DemoSession session, Student input)
    {
        RequireRole(session, UserRole.PhongQLHV);
        lock (_gate)
        {
            var student = ValidateStudent(input);
            if (_data.Students.Any(x => x.MaHocVien.Equals(student.MaHocVien, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Mã học viên đã tồn tại.");
            _data.Students.Add(student);
            LogUnsafe(session, "Thêm học viên", student.MaHocVien);
            SaveUnsafe();
            return student;
        }
    }

    public void UpdateStudent(DemoSession session, string originalCode, Student input)
    {
        RequireRole(session, UserRole.PhongQLHV);
        lock (_gate)
        {
            var student = _data.Students.FirstOrDefault(x => x.MaHocVien == originalCode)
                ?? throw new ArgumentException("Không tìm thấy học viên.");
            var updated = ValidateStudent(input);
            if (!originalCode.Equals(updated.MaHocVien, StringComparison.OrdinalIgnoreCase)
                && _data.Students.Any(x => x.MaHocVien.Equals(updated.MaHocVien, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Mã học viên mới đã tồn tại.");
            student.MaHocVien = updated.MaHocVien;
            student.HoTen = updated.HoTen;
            student.Lop = updated.Lop;
            student.TrungDoi = updated.TrungDoi;
            foreach (var request in _data.Requests.Where(x => x.MaHocVien == originalCode))
            {
                request.MaHocVien = student.MaHocVien;
                request.HoTen = student.HoTen;
                request.Lop = student.Lop;
                request.TrungDoi = student.TrungDoi;
            }
            LogUnsafe(session, "Sửa học viên", student.MaHocVien);
            SaveUnsafe();
        }
    }

    public void DeleteStudent(DemoSession session, string studentCode)
    {
        RequireRole(session, UserRole.PhongQLHV);
        lock (_gate)
        {
            var student = _data.Students.FirstOrDefault(x => x.MaHocVien == studentCode)
                ?? throw new ArgumentException("Không tìm thấy học viên.");
            if (_data.Requests.Any(x => x.MaHocVien == studentCode))
                throw new InvalidOperationException("Học viên đã có đơn nghỉ phép, không thể xóa để tránh mất liên kết hồ sơ.");
            _data.Students.Remove(student);
            LogUnsafe(session, "Xóa học viên", studentCode);
            SaveUnsafe();
        }
    }

    private static Student ValidateStudent(Student input)
    {
        var code = input.MaHocVien?.Trim() ?? "";
        var name = input.HoTen?.Trim() ?? "";
        var lop = input.Lop?.Trim() ?? "";
        var trungDoi = input.TrungDoi?.Trim() ?? "";
        if (code.Length is < 1 or > 30 || name.Length is < 1 or > 100
            || lop.Length is < 1 or > 60 || trungDoi.Length is < 1 or > 80)
            throw new ArgumentException("Mã, họ tên, lớp và trung đội phải được nhập đầy đủ, đúng độ dài.");
        return new Student { MaHocVien = code, HoTen = name, Lop = lop, TrungDoi = trungDoi };
    }

    private void RegisterManualStudentUnsafe(LeaveInput input, Student student)
    {
        if (input.MaHocVien == "__manual__" && !_data.Students.Any(x => x.MaHocVien == student.MaHocVien))
            _data.Students.Add(student);
    }

    public byte[] ExportExcel(DemoSession session, IEnumerable<LeaveRequest> rows)
    {
        RequireLoggedIn(session);
        if (!session.CanExport)
            throw new UnauthorizedAccessException("Vai trò hiện tại không được xuất Excel.");
        // Chỉ xuất danh sách do repository tự truy xuất, không sử dụng dữ liệu client không kiểm soát.
        var allowedIds = GetRequests(session).Select(x => x.Id).ToHashSet();
        return ExcelExporter.Build(rows.Where(x => allowedIds.Contains(x.Id)).ToList());
    }

    private Student ValidateInput(LeaveInput input)
    {
        if (string.IsNullOrWhiteSpace(input.TrungDoi) || input.TrungDoi.Length > 80)
            throw new ArgumentException("Vui lòng nhập trung đội (tối đa 80 ký tự).");
        if (input.DenThoiDiem <= input.TuThoiDiem)
            throw new ArgumentException("Thời gian kết thúc phải sau thời gian bắt đầu.");
        if ((input.DenThoiDiem - input.TuThoiDiem).TotalDays > 60)
            throw new ArgumentException("Đơn nghỉ không được vượt quá 60 ngày.");
        if (string.IsNullOrWhiteSpace(input.LyDo) || input.LyDo.Length > 500)
            throw new ArgumentException("Lý do nghỉ phải có nội dung và không vượt quá 500 ký tự.");
        if (string.IsNullOrWhiteSpace(input.DiaChiNoiNghi) || input.DiaChiNoiNghi.Length > 300)
            throw new ArgumentException("Địa chỉ nơi nghỉ phải có nội dung và không vượt quá 300 ký tự.");
        if (input.MaHocVien == "__manual__")
        {
            var manual = ValidateStudent(new Student
            {
                MaHocVien = input.MaHocVienTuNhap,
                HoTen = input.TenHocVienTuNhap,
                Lop = input.LopTuNhap,
                TrungDoi = input.TrungDoi
            });
            if (_data.Students.Any(x => x.MaHocVien.Equals(manual.MaHocVien, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Mã học viên này đã tồn tại, vui lòng chọn học viên từ danh sách.");
            return manual;
        }
        if (string.IsNullOrWhiteSpace(input.MaHocVien))
            throw new ArgumentException("Vui lòng chọn học viên.");
        return _data.Students.FirstOrDefault(x => x.MaHocVien == input.MaHocVien)
            ?? throw new ArgumentException("Mã học viên không tồn tại.");
    }

    private static void RequireLoggedIn(DemoSession session)
    {
        if (!session.IsAuthenticated)
            throw new UnauthorizedAccessException("Vui lòng đăng nhập trước khi thao tác.");
    }

    private static void RequireRole(DemoSession session, UserRole role)
    {
        RequireLoggedIn(session);
        if (session.Role != role)
            throw new UnauthorizedAccessException("Tài khoản không có quyền thực hiện thao tác này.");
    }

    private LeaveRequest FindUnsafe(int id) => _data.Requests.FirstOrDefault(x => x.Id == id)
        ?? throw new ArgumentException("Không tìm thấy đơn nghỉ phép.");

    private static void RequirePending(LeaveRequest request)
    {
        if (request.TrangThai != LeaveStatuses.WaitingTrungDoi)
            throw new InvalidOperationException("Đơn đã được xét duyệt, không thể sửa, xóa hoặc xét duyệt lần nữa.");
    }

    private void LogUnsafe(DemoSession session, string action, string detail)
    {
        _data.Audit.Add(new AuditEntry
        {
            At = DateTime.Now,
            User = session.UserName,
            Action = action,
            Detail = detail
        });
    }

    private void SaveUnsafe()
    {
        string temp = _file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_data, JsonOptions));
        File.Move(temp, _file, true);
    }

    private static RepositoryData SeedData()
    {
        var students = new List<Student>
        {
            new() { MaHocVien = "HV24001", HoTen = "Nguyễn Văn An", Lop = "ANM K56", TrungDoi = "Trung đội 1" },
            new() { MaHocVien = "HV24002", HoTen = "Trần Minh Đức", Lop = "ANM K56", TrungDoi = "Trung đội 1" },
            new() { MaHocVien = "HV24003", HoTen = "Lê Thu Hà", Lop = "ANM K56", TrungDoi = "Trung đội 1" },
            new() { MaHocVien = "HV24004", HoTen = "Phạm Quốc Bảo", Lop = "ANM K56", TrungDoi = "Trung đội 2" },
            new() { MaHocVien = "HV24005", HoTen = "Hoàng Ngọc Mai", Lop = "ANTT K55", TrungDoi = "Trung đội 2" },
            new() { MaHocVien = "HV24006", HoTen = "Đỗ Thị Quỳnh", Lop = "ANTT K55", TrungDoi = "Trung đội 2" },
            new() { MaHocVien = "HV24007", HoTen = "Bùi Anh Tuấn", Lop = "ANM K55", TrungDoi = "Trung đội 3" },
            new() { MaHocVien = "HV24008", HoTen = "Ngô Minh Khang", Lop = "ANM K55", TrungDoi = "Trung đội 3" },
            new() { MaHocVien = "HV24009", HoTen = "Vũ Đức Duy", Lop = "ANM K56", TrungDoi = "Trung đội 3" },
            new() { MaHocVien = "HV24010", HoTen = "Nguyễn Thị Lan", Lop = "ANTT K56", TrungDoi = "Trung đội 4" },
            new() { MaHocVien = "HV24011", HoTen = "Đặng Quang Huy", Lop = "ANTT K56", TrungDoi = "Trung đội 4" },
            new() { MaHocVien = "HV24012", HoTen = "Trương Bảo Ngọc", Lop = "ANM K56", TrungDoi = "Trung đội 4" }
        };
        string[] reasons =
        {
            "Về quê giải quyết việc gia đình", "Khám bệnh theo lịch hẹn",
            "Có việc riêng cần giải quyết", "Nghỉ điều trị theo chỉ định",
            "Thăm người thân bị ốm", "Giải quyết công việc cá nhân"
        };
        string[] addresses =
        {
            "Quận Hà Đông, Hà Nội", "Thành phố Bắc Ninh, Bắc Ninh",
            "Thành phố Hải Phòng", "Thành phố Nam Định",
            "Thành phố Thái Nguyên", "Thành phố Thanh Hóa"
        };
        var requests = new List<LeaveRequest>();
        for (int i = 0; i < 20; i++)
        {
            var student = students[i % students.Count];
            var from = DateTime.Today.AddDays(-i * 2 - 1).AddHours(7);
            var to = from.AddHours(i % 3 == 0 ? 8 : 32);
            string status = i % 6 == 0 ? LeaveStatuses.Rejected
                : i % 3 == 0 ? LeaveStatuses.Approved
                : i % 2 == 0 ? LeaveStatuses.WaitingPhong
                : LeaveStatuses.WaitingTrungDoi;
            requests.Add(new LeaveRequest
            {
                Id = i + 1,
                MaDon = $"NP{DateTime.Today:yyyy}-{i + 1:0000}",
                MaHocVien = student.MaHocVien,
                HoTen = student.HoTen,
                Lop = student.Lop,
                TrungDoi = student.TrungDoi,
                TuThoiDiem = from,
                DenThoiDiem = to,
                LyDo = reasons[i % reasons.Length],
                DiaChiNoiNghi = addresses[i % addresses.Length],
                TrangThai = status,
                NgayTao = from.AddDays(-2),
                NguoiTao = "trungdoi",
                NguoiDuyetTrungDoi = status == LeaveStatuses.WaitingTrungDoi ? "" : "trungdoi",
                NgayDuyetTrungDoi = status == LeaveStatuses.WaitingTrungDoi ? null : from.AddDays(-1),
                NguoiDuyetPhong = status == LeaveStatuses.Approved ? "phongqlhv" : "",
                NgayDuyetPhong = status == LeaveStatuses.Approved ? from.AddHours(-8) : null,
                NguoiDuyet = status == LeaveStatuses.Approved ? "phongqlhv" : status == LeaveStatuses.WaitingTrungDoi ? "" : "trungdoi",
                NgayDuyet = status == LeaveStatuses.WaitingTrungDoi ? null : from.AddDays(-1),
                GhiChuDuyet = status == "Từ chối" ? "Thông tin chưa đủ điều kiện xét duyệt." : ""
            });
        }
        return new RepositoryData
        {
            Students = students,
            Requests = requests,
            NextId = 21,
            Audit = new List<AuditEntry>
            {
                new() { User = "system", Action = "Khởi tạo dữ liệu", Detail = "Tạo dữ liệu giả lập cho bài tập lớn." }
            }
        };
    }
}

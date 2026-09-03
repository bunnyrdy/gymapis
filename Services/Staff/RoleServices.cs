using GymApis.Repos;
using GymApis.Services.Storage;

namespace GymApis.Services.Staff;

/// <summary>
/// The two concrete roles. Each is three constants over StaffServiceBase —
/// which is the point: adding Managers later is another five lines, not another
/// copy of the CRUD.
/// </summary>
public class ReceptionistService : StaffServiceBase, IReceptionistService
{
    protected override string Role => "receptionist";
    protected override string CodePrefix => "REC-";
    protected override string? TagCategory => "responsibility";

    public ReceptionistService(GymDbContext db, IPhotoStorage photos, ICurrentUser actor,
        ILogger<ReceptionistService> log) : base(db, photos, actor, log) { }
}

public class TrainerService : StaffServiceBase, ITrainerService
{
    protected override string Role => "trainer";
    protected override string CodePrefix => "TRN-";

    // Trainers carry a single `specialization` string (the design shows one
    // select), not a set of tags — so no tag category.
    public TrainerService(GymDbContext db, IPhotoStorage photos, ICurrentUser actor,
        ILogger<TrainerService> log) : base(db, photos, actor, log) { }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Librarion.Core.Data.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    UserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerProperties",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerProperties", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OwnerId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Locked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => new { x.Level, x.OwnerId, x.Key });
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoverTemplatesOff = table.Column<int>(type: "integer", nullable: false),
                    DuplicatesBuilt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateGroups",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(600)", maxLength: 600, nullable: false),
                    KeyHash = table.Column<long>(type: "bigint", nullable: false),
                    Noise = table.Column<bool>(type: "boolean", nullable: false),
                    BookCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DuplicateGroups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Groups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BuiltIn = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    ExternalName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AllLibraries = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Groups_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Libraries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    BookCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Libraries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Libraries_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MergeLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    SourceName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: false),
                    TargetName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Links = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: true),
                    Undone = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MergeLogs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SortName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Shorthand = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Color = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    IsCategory = table.Column<bool>(type: "boolean", nullable: false),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false),
                    DisambiguationParentId = table.Column<int>(type: "integer", nullable: true),
                    FieldKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FieldId = table.Column<int>(type: "integer", nullable: true),
                    BuiltIn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tags_Tags_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tags_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskRuns",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: true),
                    Task = table.Column<byte>(type: "smallint", nullable: false),
                    Trigger = table.Column<byte>(type: "smallint", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true),
                    Started = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Finished = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<byte>(type: "smallint", nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Counts = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskRuns_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TenantDisabledRights",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Right = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantDisabledRights", x => new { x.TenantId, x.Right });
                    table.ForeignKey(
                        name: "FK_TenantDisabledRights_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: true),
                    UserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedUserName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Source = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IsServerAdmin = table.Column<bool>(type: "boolean", nullable: false),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    SecurityStamp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FailedSignIns = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSignIn = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Users_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupRights",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    Right = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupRights", x => new { x.GroupId, x.Right });
                    table.ForeignKey(
                        name: "FK_GroupRights_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Books",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SortTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SearchText = table.Column<string>(type: "text", nullable: false),
                    SortAuthor = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SeriesId = table.Column<int>(type: "integer", nullable: true),
                    SeriesNumber = table.Column<double>(type: "double precision", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Rating = table.Column<byte>(type: "smallint", nullable: true),
                    Added = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CoverSource = table.Column<int>(type: "integer", nullable: false),
                    GeneratedTemplate = table.Column<short>(type: "smallint", nullable: true),
                    GeneratedVariant = table.Column<short>(type: "smallint", nullable: true),
                    CoverFile = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CoverState = table.Column<int>(type: "integer", nullable: false),
                    MergedIntoId = table.Column<int>(type: "integer", nullable: true),
                    MissingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MetadataSource = table.Column<byte>(type: "smallint", nullable: false),
                    ImportHash = table.Column<long>(type: "bigint", nullable: true),
                    WorkKey = table.Column<long>(type: "bigint", nullable: true),
                    Edited = table.Column<byte>(type: "smallint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Books", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Books_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Books_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupLibraries",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupLibraries", x => new { x.GroupId, x.LibraryId });
                    table.ForeignKey(
                        name: "FK_GroupLibraries_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupLibraries_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LibraryRoots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryRoots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryRoots_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaskSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    Task = table.Column<byte>(type: "smallint", nullable: false),
                    Kind = table.Column<byte>(type: "smallint", nullable: false),
                    ImportKind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Source = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Files = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Start = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    Days = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Updated = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRun = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskSchedules_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MergeLogLinks",
                columns: table => new
                {
                    LogId = table.Column<long>(type: "bigint", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: true),
                    Number = table.Column<double>(type: "double precision", nullable: true),
                    Kept = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MergeLogLinks", x => new { x.LogId, x.BookId, x.Source });
                    table.ForeignKey(
                        name: "FK_MergeLogLinks_MergeLogs_LogId",
                        column: x => x.LogId,
                        principalTable: "MergeLogs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FieldKeyAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FieldId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FieldKeyAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FieldKeyAliases_Tags_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_FieldKeyAliases_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupTagFilters",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Deny = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupTagFilters", x => new { x.GroupId, x.TagId });
                    table.ForeignKey(
                        name: "FK_GroupTagFilters_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupTagFilters_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportTagMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    FieldId = table.Column<int>(type: "integer", nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: true),
                    Ignored = table.Column<bool>(type: "boolean", nullable: false),
                    Reviewed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportTagMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportTagMappings_Tags_FieldId",
                        column: x => x.FieldId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportTagMappings_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportTagMappings_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagAliases",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagAliases_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagClosure",
                columns: table => new
                {
                    AncestorId = table.Column<int>(type: "integer", nullable: false),
                    DescendantId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Depth = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagClosure", x => new { x.AncestorId, x.DescendantId });
                    table.ForeignKey(
                        name: "FK_TagClosure_Tags_AncestorId",
                        column: x => x.AncestorId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagClosure_Tags_DescendantId",
                        column: x => x.DescendantId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagCounts",
                columns: table => new
                {
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    BookCount = table.Column<int>(type: "integer", nullable: false),
                    TotalCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagCounts", x => new { x.LibraryId, x.TagId });
                    table.ForeignKey(
                        name: "FK_TagCounts_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagCounts_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TagParents",
                columns: table => new
                {
                    ChildId = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagParents", x => new { x.ChildId, x.ParentId });
                    table.ForeignKey(
                        name: "FK_TagParents_Tags_ChildId",
                        column: x => x.ChildId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TagParents_Tags_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.GroupId, x.UserId });
                    table.ForeignKey(
                        name: "FK_GroupMembers_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookIdentifiers",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Scheme = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Value = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookIdentifiers", x => new { x.BookId, x.Scheme, x.Value });
                    table.ForeignKey(
                        name: "FK_BookIdentifiers_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookProperties",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookProperties", x => new { x.BookId, x.Name });
                    table.ForeignKey(
                        name: "FK_BookProperties_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookTags",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<byte>(type: "smallint", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: true),
                    Number = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTags", x => new { x.BookId, x.TagId, x.Source });
                    table.ForeignKey(
                        name: "FK_BookTags_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookTagSuppressions",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookTagSuppressions", x => new { x.BookId, x.TagId });
                    table.ForeignKey(
                        name: "FK_BookTagSuppressions_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookTagSuppressions_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DuplicateMembers",
                columns: table => new
                {
                    GroupId = table.Column<long>(type: "bigint", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DuplicateMembers", x => new { x.GroupId, x.BookId });
                    table.ForeignKey(
                        name: "FK_DuplicateMembers_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DuplicateMembers_DuplicateGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "DuplicateGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotDuplicates",
                columns: table => new
                {
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    OtherId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotDuplicates", x => new { x.BookId, x.OtherId });
                    table.ForeignKey(
                        name: "FK_NotDuplicates_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NotDuplicates_Books_OtherId",
                        column: x => x.OtherId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Archives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    RootId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PathKey = table.Column<long>(type: "bigint", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Fingerprint = table.Column<long>(type: "bigint", nullable: true),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    ScanState = table.Column<int>(type: "integer", nullable: false),
                    MissingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Archives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Archives_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Archives_LibraryRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "LibraryRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Folders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    RootId = table.Column<int>(type: "integer", nullable: false),
                    ParentId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    PathKey = table.Column<long>(type: "bigint", nullable: false),
                    ArchiveId = table.Column<int>(type: "integer", nullable: true),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    TotalFileCount = table.Column<int>(type: "integer", nullable: false),
                    FolderCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Folders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Folders_Archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "Archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Folders_Folders_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Folders_LibraryRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "LibraryRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    RootId = table.Column<int>(type: "integer", nullable: true),
                    ArchiveId = table.Column<int>(type: "integer", nullable: true),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PathKey = table.Column<long>(type: "bigint", nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Crc32 = table.Column<long>(type: "bigint", nullable: true),
                    Modified = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Locator = table.Column<long>(type: "bigint", nullable: true),
                    Method = table.Column<short>(type: "smallint", nullable: true),
                    PackedSize = table.Column<long>(type: "bigint", nullable: true),
                    ContentHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true),
                    MissingSince = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookFiles_Archives_ArchiveId",
                        column: x => x.ArchiveId,
                        principalTable: "Archives",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookFiles_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookFiles_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookFiles_LibraryRoots_RootId",
                        column: x => x.RootId,
                        principalTable: "LibraryRoots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DefaultTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    LibraryId = table.Column<int>(type: "integer", nullable: false),
                    FolderId = table.Column<int>(type: "integer", nullable: true),
                    TagId = table.Column<int>(type: "integer", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DefaultTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DefaultTags_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DefaultTags_Libraries_LibraryId",
                        column: x => x.LibraryId,
                        principalTable: "Libraries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DefaultTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Archives_LibraryId",
                table: "Archives",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_Archives_RootId_PathKey",
                table: "Archives",
                columns: new[] { "RootId", "PathKey" });

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_ArchiveId_PathKey",
                table: "BookFiles",
                columns: new[] { "ArchiveId", "PathKey" });

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_BookId",
                table: "BookFiles",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_FolderId_BookId",
                table: "BookFiles",
                columns: new[] { "FolderId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_LibraryId_Size_Id",
                table: "BookFiles",
                columns: new[] { "LibraryId", "Size", "Id" },
                filter: "\"MissingSince\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_RootId_PathKey",
                table: "BookFiles",
                columns: new[] { "RootId", "PathKey" });

            migrationBuilder.CreateIndex(
                name: "IX_BookFiles_TenantId_Size_Crc32",
                table: "BookFiles",
                columns: new[] { "TenantId", "Size", "Crc32" });

            migrationBuilder.CreateIndex(
                name: "IX_BookIdentifiers_TenantId_Scheme_Value",
                table: "BookIdentifiers",
                columns: new[] { "TenantId", "Scheme", "Value" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_LibraryId_Added_Id",
                table: "Books",
                columns: new[] { "LibraryId", "Added", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_LibraryId_MissingSince_Id",
                table: "Books",
                columns: new[] { "LibraryId", "MissingSince", "Id" },
                filter: "\"MissingSince\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Books_LibraryId_SortAuthor_Id",
                table: "Books",
                columns: new[] { "LibraryId", "SortAuthor", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_LibraryId_SortTitle_Id",
                table: "Books",
                columns: new[] { "LibraryId", "SortTitle", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_SeriesId_SeriesNumber_Id",
                table: "Books",
                columns: new[] { "SeriesId", "SeriesNumber", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_TenantId_Added_Id",
                table: "Books",
                columns: new[] { "TenantId", "Added", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_TenantId_CoverFile",
                table: "Books",
                columns: new[] { "TenantId", "CoverFile" },
                filter: "\"CoverFile\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Books_TenantId_SortAuthor_Id",
                table: "Books",
                columns: new[] { "TenantId", "SortAuthor", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_TenantId_SortTitle_Id",
                table: "Books",
                columns: new[] { "TenantId", "SortTitle", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Books_TenantId_WorkKey",
                table: "Books",
                columns: new[] { "TenantId", "WorkKey" });

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_TagId_BookId",
                table: "BookTags",
                columns: new[] { "TagId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_BookTags_TagId_Number_BookId",
                table: "BookTags",
                columns: new[] { "TagId", "Number", "BookId" },
                filter: "\"Number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookTagSuppressions_TagId",
                table: "BookTagSuppressions",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_DefaultTags_FolderId",
                table: "DefaultTags",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_DefaultTags_LibraryId_FolderId_TagId",
                table: "DefaultTags",
                columns: new[] { "LibraryId", "FolderId", "TagId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DefaultTags_TagId",
                table: "DefaultTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateGroups_TenantId_Level_KeyHash",
                table: "DuplicateGroups",
                columns: new[] { "TenantId", "Level", "KeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateMembers_BookId",
                table: "DuplicateMembers",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_DuplicateMembers_TenantId_BookId",
                table: "DuplicateMembers",
                columns: new[] { "TenantId", "BookId" });

            migrationBuilder.CreateIndex(
                name: "IX_FieldKeyAliases_FieldId",
                table: "FieldKeyAliases",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_FieldKeyAliases_TenantId_Key",
                table: "FieldKeyAliases",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ArchiveId",
                table: "Folders",
                column: "ArchiveId");

            migrationBuilder.CreateIndex(
                name: "IX_Folders_ParentId_Name_Id",
                table: "Folders",
                columns: new[] { "ParentId", "Name", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Folders_RootId_PathKey",
                table: "Folders",
                columns: new[] { "RootId", "PathKey" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupLibraries_LibraryId",
                table: "GroupLibraries",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_UserId",
                table: "GroupMembers",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Groups_TenantId_Name",
                table: "Groups",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupTagFilters_TagId",
                table: "GroupTagFilters",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportTagMappings_FieldId",
                table: "ImportTagMappings",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportTagMappings_TagId",
                table: "ImportTagMappings",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportTagMappings_TenantId_FieldId_TagId",
                table: "ImportTagMappings",
                columns: new[] { "TenantId", "FieldId", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportTagMappings_TenantId_Source_FieldId_NormalizedName",
                table: "ImportTagMappings",
                columns: new[] { "TenantId", "Source", "FieldId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_Libraries_TenantId_Name",
                table: "Libraries",
                columns: new[] { "TenantId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryRoots_LibraryId",
                table: "LibraryRoots",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeLogs_ParentId",
                table: "MergeLogs",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeLogs_SourceId",
                table: "MergeLogs",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeLogs_TargetId",
                table: "MergeLogs",
                column: "TargetId");

            migrationBuilder.CreateIndex(
                name: "IX_MergeLogs_TenantId_Id",
                table: "MergeLogs",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_NotDuplicates_OtherId",
                table: "NotDuplicates",
                column: "OtherId");

            migrationBuilder.CreateIndex(
                name: "IX_NotDuplicates_TenantId_OtherId",
                table: "NotDuplicates",
                columns: new[] { "TenantId", "OtherId" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_TenantId_Id",
                table: "SecurityEvents",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Time",
                table: "SecurityEvents",
                column: "Time");

            migrationBuilder.CreateIndex(
                name: "IX_Settings_TenantId",
                table: "Settings",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TagAliases_TagId",
                table: "TagAliases",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagAliases_TenantId_NormalizedName",
                table: "TagAliases",
                columns: new[] { "TenantId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_TagClosure_DescendantId_AncestorId",
                table: "TagClosure",
                columns: new[] { "DescendantId", "AncestorId" });

            migrationBuilder.CreateIndex(
                name: "IX_TagCounts_LibraryId_BookCount_TagId",
                table: "TagCounts",
                columns: new[] { "LibraryId", "BookCount", "TagId" });

            migrationBuilder.CreateIndex(
                name: "IX_TagCounts_TagId",
                table: "TagCounts",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_TagParents_ParentId",
                table: "TagParents",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_FieldId",
                table: "Tags",
                column: "FieldId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_FieldId_NormalizedName",
                table: "Tags",
                columns: new[] { "TenantId", "FieldId", "NormalizedName" });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_FieldId_SortName_Id",
                table: "Tags",
                columns: new[] { "TenantId", "FieldId", "SortName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_FieldKey",
                table: "Tags",
                columns: new[] { "TenantId", "FieldKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_IsHidden",
                table: "Tags",
                columns: new[] { "TenantId", "IsHidden" },
                filter: "\"IsHidden\"");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_TenantId_Shorthand",
                table: "Tags",
                columns: new[] { "TenantId", "Shorthand" },
                filter: "\"Shorthand\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_Outcome",
                table: "TaskRuns",
                column: "Outcome");

            migrationBuilder.CreateIndex(
                name: "IX_TaskRuns_TenantId_Started_Id",
                table: "TaskRuns",
                columns: new[] { "TenantId", "Started", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskSchedules_LibraryId",
                table: "TaskSchedules",
                column: "LibraryId");

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Slug",
                table: "Tenants",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedUserName",
                table: "Users",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Source_ExternalId",
                table: "Users",
                columns: new[] { "Source", "ExternalId" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_TenantId_NormalizedUserName",
                table: "Users",
                columns: new[] { "TenantId", "NormalizedUserName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookFiles");

            migrationBuilder.DropTable(
                name: "BookIdentifiers");

            migrationBuilder.DropTable(
                name: "BookProperties");

            migrationBuilder.DropTable(
                name: "BookTags");

            migrationBuilder.DropTable(
                name: "BookTagSuppressions");

            migrationBuilder.DropTable(
                name: "DefaultTags");

            migrationBuilder.DropTable(
                name: "DuplicateMembers");

            migrationBuilder.DropTable(
                name: "FieldKeyAliases");

            migrationBuilder.DropTable(
                name: "GroupLibraries");

            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "GroupRights");

            migrationBuilder.DropTable(
                name: "GroupTagFilters");

            migrationBuilder.DropTable(
                name: "ImportTagMappings");

            migrationBuilder.DropTable(
                name: "MergeLogLinks");

            migrationBuilder.DropTable(
                name: "NotDuplicates");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "ServerProperties");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "TagAliases");

            migrationBuilder.DropTable(
                name: "TagClosure");

            migrationBuilder.DropTable(
                name: "TagCounts");

            migrationBuilder.DropTable(
                name: "TagParents");

            migrationBuilder.DropTable(
                name: "TaskRuns");

            migrationBuilder.DropTable(
                name: "TaskSchedules");

            migrationBuilder.DropTable(
                name: "TenantDisabledRights");

            migrationBuilder.DropTable(
                name: "Folders");

            migrationBuilder.DropTable(
                name: "DuplicateGroups");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Groups");

            migrationBuilder.DropTable(
                name: "MergeLogs");

            migrationBuilder.DropTable(
                name: "Books");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Archives");

            migrationBuilder.DropTable(
                name: "LibraryRoots");

            migrationBuilder.DropTable(
                name: "Libraries");

            migrationBuilder.DropTable(
                name: "Tenants");
        }
    }
}

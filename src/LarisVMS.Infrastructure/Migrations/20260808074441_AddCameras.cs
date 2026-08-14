using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LarisVMS.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCameras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CameraGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ParentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    MaterializedPath = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CameraGroups_CameraGroups_ParentId",
                        column: x => x.ParentId,
                        principalTable: "CameraGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Cameras",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NodeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    GroupId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    OnvifPort = table.Column<int>(type: "int", nullable: false),
                    DeviceServiceUri = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Password = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Manufacturer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    FirmwareVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SerialNumber = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    QuotaBytes = table.Column<long>(type: "bigint", nullable: true),
                    LensType = table.Column<int>(type: "int", nullable: false),
                    DewarpConfigJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastProbedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cameras", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Cameras_CameraGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "CameraGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CameraCapabilities",
                columns: table => new
                {
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProfileS = table.Column<bool>(type: "bit", nullable: false),
                    ProfileT = table.Column<bool>(type: "bit", nullable: false),
                    ProfileG = table.Column<bool>(type: "bit", nullable: false),
                    ProfileM = table.Column<bool>(type: "bit", nullable: false),
                    HasPtz = table.Column<bool>(type: "bit", nullable: false),
                    HasAudioOut = table.Column<bool>(type: "bit", nullable: false),
                    HasRelayOutputs = table.Column<bool>(type: "bit", nullable: false),
                    HasDigitalInputs = table.Column<bool>(type: "bit", nullable: false),
                    HasAnalyticsMetadata = table.Column<bool>(type: "bit", nullable: false),
                    HasImaging = table.Column<bool>(type: "bit", nullable: false),
                    HasEvents = table.Column<bool>(type: "bit", nullable: false),
                    HasMedia2 = table.Column<bool>(type: "bit", nullable: false),
                    RawProbeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProbedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraCapabilities", x => x.CameraId);
                    table.ForeignKey(
                        name: "FK_CameraCapabilities_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CameraStreams",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CameraId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    RtspUri = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    ProfileToken = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Codec = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Width = table.Column<int>(type: "int", nullable: true),
                    Height = table.Column<int>(type: "int", nullable: true),
                    Fps = table.Column<int>(type: "int", nullable: true),
                    BitrateKbps = table.Column<int>(type: "int", nullable: true),
                    HasAudio = table.Column<bool>(type: "bit", nullable: false),
                    AudioCodec = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraStreams", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CameraStreams_Cameras_CameraId",
                        column: x => x.CameraId,
                        principalTable: "Cameras",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraGroups_ParentId",
                table: "CameraGroups",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_GroupId",
                table: "Cameras",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Cameras_Name",
                table: "Cameras",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_CameraStreams_CameraId_Role",
                table: "CameraStreams",
                columns: new[] { "CameraId", "Role" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraCapabilities");

            migrationBuilder.DropTable(
                name: "CameraStreams");

            migrationBuilder.DropTable(
                name: "Cameras");

            migrationBuilder.DropTable(
                name: "CameraGroups");
        }
    }
}

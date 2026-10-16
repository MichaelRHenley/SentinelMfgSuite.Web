START TRANSACTION;

CREATE TABLE "Assessment" (
    "Id" uuid NOT NULL,
    "Title" character varying(200),
    "SiteName" character varying(200),
    "ContactEmail" character varying(320),
    "Status" smallint NOT NULL,
    "CreatedUtc" timestamp with time zone NOT NULL,
    "UpdatedUtc" timestamp with time zone NOT NULL,
    "Notes" character varying(2000),
    "OwnerUserId" character varying(200),
    CONSTRAINT "PK_Assessment" PRIMARY KEY ("Id")
);

CREATE TABLE "AssessmentInvitation" (
    "Id" uuid NOT NULL,
    "AssessmentId" uuid NOT NULL,
    "Email" character varying(320) NOT NULL,
    "TokenHash" character varying(200) NOT NULL,
    "CreatedUtc" timestamp with time zone NOT NULL,
    "ExpiresUtc" timestamp with time zone NOT NULL,
    "RevokedUtc" timestamp with time zone,
    "RevokedByUserId" text,
    "LastAccessUtc" timestamp with time zone,
    "AccessCount" integer NOT NULL,
    "CreatedByUserId" text,
    CONSTRAINT "PK_AssessmentInvitation" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssessmentInvitation_Assessment_AssessmentId" FOREIGN KEY ("AssessmentId") REFERENCES "Assessment" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "AssessmentResponse" (
    "Id" uuid NOT NULL,
    "AssessmentId" uuid NOT NULL,
    "InvitationId" uuid NOT NULL,
    "Data" jsonb NOT NULL,
    "IsDraft" boolean NOT NULL,
    "LastSavedUtc" timestamp with time zone NOT NULL,
    "SubmittedUtc" timestamp with time zone,
    "ConcurrencyStamp" character varying(100) NOT NULL,
    CONSTRAINT "PK_AssessmentResponse" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AssessmentResponse_AssessmentInvitation_InvitationId" FOREIGN KEY ("InvitationId") REFERENCES "AssessmentInvitation" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AssessmentResponse_Assessment_AssessmentId" FOREIGN KEY ("AssessmentId") REFERENCES "Assessment" ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_AssessmentInvitation_AssessmentId" ON "AssessmentInvitation" ("AssessmentId");

CREATE UNIQUE INDEX "IX_AssessmentInvitation_TokenHash" ON "AssessmentInvitation" ("TokenHash");

CREATE INDEX "IX_AssessmentResponse_AssessmentId" ON "AssessmentResponse" ("AssessmentId");

CREATE UNIQUE INDEX "IX_AssessmentResponse_InvitationId" ON "AssessmentResponse" ("InvitationId");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261012161320_AddAssessmentFoundation', '8.0.11');

COMMIT;


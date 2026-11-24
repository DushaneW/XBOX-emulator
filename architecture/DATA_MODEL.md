# Initial data model

## GameEntry
- Id (UUID)
- DisplayName
- Generation (OriginalXbox | Xbox360 | XboxOne | SeriesXS | Other)
- UserSuppliedContentPath (optional)
- ContentFingerprint (optional; hash only where lawful)
- CoverArtPath (optional; user-provided or properly licensed)
- MetadataSource
- AddedAt
- LastLaunchedAt
- UserNotes

## RuntimeDescriptor
- RuntimeId
- Generation
- DisplayName
- Version
- ImplementationState (NotImplemented | ResearchOnly | Experimental | Available)
- Capabilities[]
- LicenseNotice
- DocumentationUri

## CompatibilityReport
- ReportId
- GameEntryId
- RuntimeId
- RuntimeVersion
- TestDate
- HostOS
- CPU/GPU/RAM/driver details
- TestAssetFingerprint
- TestProcedure
- Outcome
- AverageFPS (nullable, measured only)
- FrameTimeSummary (nullable)
- AudioResult
- InputResult
- GraphicsIssues[]
- CrashDetails (redacted)
- EvidenceReferences[]
- Reviewer
- ReproducibilityNotes

## UserProfile
- Id
- DisplayName
- AvatarPath (optional)
- Preferences
- CreatedAt

Do not store Microsoft passwords, refresh tokens in plaintext, console keys, or proprietary secrets.

using Saas.Platform.Api.Domain;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class TenantDomainValidationTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryRegister_WithValidSyntheticData_NormalizesAndCreatesTenantInPendienteAdmin()
    {
        var ok = Tenant.TryRegister(
            publicId: "  Clinica-San-Mateo-01  ",
            name: "  Clínica Sintética San Mateo  ",
            contactEmail: "  Admin.SanMateo@Clinica-Sintetica.Test  ",
            maxActiveAccounts: 25,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.NotNull(tenant);
        Assert.NotEqual(Guid.Empty, tenant.Id);
        Assert.Equal("clinica-san-mateo-01", tenant.PublicId);
        Assert.Equal("Clínica Sintética San Mateo", tenant.Name);
        Assert.Equal("admin.sanmateo@clinica-sintetica.test", tenant.ContactEmail);
        Assert.Equal(25, tenant.MaxActiveAccounts);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
        Assert.Equal(FixedUtcNow, tenant.CreatedAtUtc);
        Assert.Equal(FixedUtcNow, tenant.UpdatedAtUtc);
        Assert.NotEqual(Guid.Empty, tenant.ConcurrencyStamp);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("ab", "length")]
    [InlineData("clinica.norte.local", "format")]
    [InlineData("clinica_norte", "format")]
    [InlineData("clinica norte", "format")]
    [InlineData("-clinica-norte", "format")]
    [InlineData("clinica-norte-", "format")]
    [InlineData("clinica--norte", "format")]
    public void TryRegister_WithInvalidPublicId_ReturnsExpectedValidationError(string? publicId, string expectedCode)
    {
        var ok = Tenant.TryRegister(
            publicId: publicId,
            name: "Clínica Sintética Norte",
            contactEmail: "contacto@clinica-norte.test",
            maxActiveAccounts: 10,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.False(ok);
        Assert.Null(tenant);
        Assert.Contains(errors, e => e.Field == nameof(Tenant.PublicId) && e.Code == expectedCode);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("A", "length")]
    [InlineData("Clínica\nSintética", "format")]
    public void TryRegister_WithInvalidName_ReturnsExpectedValidationError(string? name, string expectedCode)
    {
        var ok = Tenant.TryRegister(
            publicId: "clinica-sur-01",
            name: name,
            contactEmail: "contacto@clinica-sur.test",
            maxActiveAccounts: 10,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.False(ok);
        Assert.Null(tenant);
        Assert.Contains(errors, e => e.Field == nameof(Tenant.Name) && e.Code == expectedCode);
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData("", "required")]
    [InlineData("   ", "required")]
    [InlineData("no-es-correo", "format")]
    [InlineData("admin@localhost", "format")]
    [InlineData("admin@.test", "format")]
    [InlineData("admin@clinica.test.", "format")]
    [InlineData("admin test@clinica.test", "format")]
    public void TryRegister_WithInvalidContactEmail_ReturnsExpectedValidationError(string? email, string expectedCode)
    {
        var ok = Tenant.TryRegister(
            publicId: "clinica-este-01",
            name: "Clínica Sintética Este",
            contactEmail: email,
            maxActiveAccounts: 10,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.False(ok);
        Assert.Null(tenant);
        Assert.Contains(errors, e => e.Field == nameof(Tenant.ContactEmail) && e.Code == expectedCode);
    }

    [Fact]
    public void TryRegister_WithMultipleInvalidFields_ReportsAllErrors()
    {
        var ok = Tenant.TryRegister(
            publicId: "x",
            name: "",
            contactEmail: "invalido",
            maxActiveAccounts: 10,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.False(ok);
        Assert.Null(tenant);
        Assert.Equal(3, errors.Count);
        Assert.Contains(errors, e => e.Field == nameof(Tenant.PublicId));
        Assert.Contains(errors, e => e.Field == nameof(Tenant.Name));
        Assert.Contains(errors, e => e.Field == nameof(Tenant.ContactEmail));
    }

    [Theory]
    [InlineData(null, "required")]
    [InlineData(0, "range")]
    [InlineData(-5, "range")]
    public void TryRegister_WithInvalidMaxActiveAccounts_ReturnsExpectedValidationError(int? quota, string expectedCode)
    {
        var ok = Tenant.TryRegister(
            publicId: "clinica-cuota-01",
            name: "Clínica Sintética Cuota",
            contactEmail: "contacto@clinica-cuota.test",
            maxActiveAccounts: quota,
            createdAtUtc: FixedUtcNow,
            out var tenant,
            out var errors);

        Assert.False(ok);
        Assert.Null(tenant);
        Assert.Contains(errors, e => e.Field == nameof(Tenant.MaxActiveAccounts) && e.Code == expectedCode);
    }

    [Fact]
    public void CanActivate_WithIdentityReadyAlone_RejectsActivationBecauseInfrastructureIsNotConfirmed()
    {
        Tenant.TryRegister(
            "clinica-prereq-01",
            "Clínica Sintética Prerrequisitos",
            "contacto@clinica-prereq.test",
            15,
            FixedUtcNow,
            out var tenant,
            out _);

        Assert.NotNull(tenant);

        var eligible = tenant.CanActivate(
            new TenantActivationPrerequisites(
                TenantDatabaseInstanceReady: false,
                ServiceDatabasesPrepared: false,
                IdentityInitialAdminReady: true,
                InitialAdminAccountId: "acct-synth-01"),
            out var unmet);

        Assert.False(eligible);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
        Assert.Contains(unmet, e => e.Field == nameof(TenantActivationPrerequisites.TenantDatabaseInstanceReady));
        Assert.Contains(unmet, e => e.Field == nameof(TenantActivationPrerequisites.ServiceDatabasesPrepared));
    }

    [Fact]
    public void CanActivate_WithMissingIdentityConfirmationOrAccountId_RejectsActivation()
    {
        Tenant.TryRegister(
            "clinica-prereq-02",
            "Clínica Sintética Sin Identity",
            "contacto@clinica-prereq-02.test",
            15,
            FixedUtcNow,
            out var tenant,
            out _);

        Assert.NotNull(tenant);

        var eligible = tenant.CanActivate(
            new TenantActivationPrerequisites(
                TenantDatabaseInstanceReady: true,
                ServiceDatabasesPrepared: true,
                IdentityInitialAdminReady: false,
                InitialAdminAccountId: "  "),
            out var unmet);

        Assert.False(eligible);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.Contains(unmet, e => e.Field == nameof(TenantActivationPrerequisites.IdentityInitialAdminReady));
        Assert.Contains(unmet, e => e.Field == nameof(TenantActivationPrerequisites.InitialAdminAccountId));
    }

    [Fact]
    public void CanActivate_WhenAllPrerequisitesConfirmed_ReturnsTrueWithoutMutatingStateInPlat02A()
    {
        Tenant.TryRegister(
            "clinica-prereq-03",
            "Clínica Sintética Completa",
            "contacto@clinica-prereq-03.test",
            15,
            FixedUtcNow,
            out var tenant,
            out _);

        Assert.NotNull(tenant);

        var eligible = tenant.CanActivate(
            new TenantActivationPrerequisites(
                TenantDatabaseInstanceReady: true,
                ServiceDatabasesPrepared: true,
                IdentityInitialAdminReady: true,
                InitialAdminAccountId: "acct-synth-03"),
            out var unmet);

        Assert.True(eligible);
        Assert.Empty(unmet);
        Assert.Equal(TenantOperationalState.PendienteAdmin, tenant.OperationalState);
        Assert.False(tenant.MayAuthenticate);
    }

    [Fact]
    public void TryCreateInitial_WithValidInitialAdmin_CreatesPendingOperationWithUuidRequestId()
    {
        var tenantId = Guid.CreateVersion7(FixedUtcNow);

        var ok = TenantProvisioningOperation.TryCreateInitial(
            tenantId,
            "  Dra. Elena Ríos  ",
            "  Elena.Rios@Clinica-Sintetica.Test  ",
            FixedUtcNow,
            out var operation,
            out var errors);

        Assert.True(ok);
        Assert.Empty(errors);
        Assert.NotNull(operation);
        Assert.NotEqual(Guid.Empty, operation.Id);
        Assert.Equal(tenantId, operation.TenantId);
        Assert.True(Guid.TryParseExact(operation.ProvisioningRequestId, "D", out var parsedRequestId));
        Assert.Equal(operation.Id, parsedRequestId);
        Assert.Equal("Dra. Elena Ríos", operation.InitialAdminFullName);
        Assert.Equal("elena.rios@clinica-sintetica.test", operation.InitialAdminEmail);
        Assert.Equal(ProvisioningOperationStatus.Pending, operation.Status);
        Assert.Equal(0, operation.AttemptCount);
        Assert.Null(operation.LastErrorCode);
        Assert.Null(operation.LastErrorSummary);
        Assert.Null(operation.LastAttemptAtUtc);
        Assert.Null(operation.CompletedAtUtc);
        Assert.True(operation.MatchesInitialAdminPayload(tenantId, "Dra. Elena Ríos", "ELENA.RIOS@CLINICA-SINTETICA.TEST"));
        Assert.False(operation.MatchesInitialAdminPayload(tenantId, "Dra. Elena Ríos", "otro@clinica-sintetica.test"));
    }

    [Theory]
    [InlineData(null, "admin@clinica.test", nameof(TenantProvisioningOperation.InitialAdminFullName), "required")]
    [InlineData("A", "admin@clinica.test", nameof(TenantProvisioningOperation.InitialAdminFullName), "length")]
    [InlineData("Admin\nNombre", "admin@clinica.test", nameof(TenantProvisioningOperation.InitialAdminFullName), "format")]
    [InlineData("Admin Sintético", null, nameof(TenantProvisioningOperation.InitialAdminEmail), "required")]
    [InlineData("Admin Sintético", "correo-invalido", nameof(TenantProvisioningOperation.InitialAdminEmail), "format")]
    public void TryCreateInitial_WithInvalidInitialAdmin_ReturnsExpectedValidationError(
        string? fullName,
        string? email,
        string expectedField,
        string expectedCode)
    {
        var ok = TenantProvisioningOperation.TryCreateInitial(
            Guid.NewGuid(),
            fullName,
            email,
            FixedUtcNow,
            out var operation,
            out var errors);

        Assert.False(ok);
        Assert.Null(operation);
        Assert.Contains(errors, e => e.Field == expectedField && e.Code == expectedCode);
    }

    [Fact]
    public void RecordRecoverableFailure_PreservesRequestIdAndAdminDataAndRedactsSecrets()
    {
        var tenantId = Guid.CreateVersion7(FixedUtcNow);
        TenantProvisioningOperation.TryCreateInitial(
            tenantId,
            "Dr. Marco Paredes",
            "marco.paredes@clinica-sintetica.test",
            FixedUtcNow,
            out var operation,
            out _);

        Assert.NotNull(operation);
        var originalId = operation.Id;
        var originalRequestId = operation.ProvisioningRequestId;

        var retryAt = FixedUtcNow.AddMinutes(2);
        operation.RecordRecoverableFailure(
            "identity_unavailable",
            "gRPC Unavailable with x-internal-service-key: SyntheticSecret123! password=TempValue456 Bearer eyJfakeToken",
            retryAt);

        Assert.Equal(originalId, operation.Id);
        Assert.Equal(originalRequestId, operation.ProvisioningRequestId);
        Assert.Equal("Dr. Marco Paredes", operation.InitialAdminFullName);
        Assert.Equal("marco.paredes@clinica-sintetica.test", operation.InitialAdminEmail);
        Assert.Equal(ProvisioningOperationStatus.FailedRetryable, operation.Status);
        Assert.Equal(1, operation.AttemptCount);
        Assert.Equal("IDENTITY_UNAVAILABLE", operation.LastErrorCode);
        Assert.Equal(retryAt, operation.LastAttemptAtUtc);
        Assert.Equal(retryAt, operation.UpdatedAtUtc);
        Assert.Null(operation.CompletedAtUtc);
        Assert.NotNull(operation.LastErrorSummary);
        Assert.DoesNotContain("SyntheticSecret123!", operation.LastErrorSummary);
        Assert.DoesNotContain("TempValue456", operation.LastErrorSummary);
        Assert.DoesNotContain("eyJfakeToken", operation.LastErrorSummary);
        Assert.Contains("[REDACTED]", operation.LastErrorSummary);
    }
}

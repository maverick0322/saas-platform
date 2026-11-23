using Saas.Platform.Api.Domain;
using Xunit;

namespace Saas.Platform.Tests;

public sealed class TenantDomainValidationTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryRegister_WithValidSyntheticData_NormalizesAndCreatesTenant()
    {
        var ok = Tenant.TryRegister(
            publicId: "  Clinica-San-Mateo-01  ",
            name: "  Clínica Sintética San Mateo  ",
            contactEmail: "  Admin.SanMateo@Clinica-Sintetica.Test  ",
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
}

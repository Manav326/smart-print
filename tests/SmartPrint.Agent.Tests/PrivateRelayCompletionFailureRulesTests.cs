using System.Net;
using Xunit;

namespace SmartPrint.Agent.Tests;

public sealed class PrivateRelayCompletionFailureRulesTests
{
    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.Conflict, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void OnlyDocumentValidationRejectionsAreTerminal(
        HttpStatusCode statusCode,
        bool expected)
    {
        Assert.Equal(expected,
            PrivateRelayCompletionFailureRules.IsPermanentDocumentRejection(statusCode));
    }

    [Theory]
    [InlineData("photo_sheet_layout_invalid", "Photo Studio")]
    [InlineData("photo_metadata_invalid", "Photo Studio")]
    [InlineData("id_document_pdf_invalid", "ID Studio")]
    [InlineData("private_document_page_analysis_invalid", "page information")]
    [InlineData("private_document_validation_mismatch", "verify")]
    [InlineData(null, "validate this PDF")]
    public void PermanentValidationFailuresProduceActionableCustomerMessages(
        string? code,
        string expectedText)
    {
        var message = PrivateRelayCompletionFailureRules.CustomerMessage(code);

        Assert.Contains(expectedText, message, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(message.Length, 1, 400);
    }
}

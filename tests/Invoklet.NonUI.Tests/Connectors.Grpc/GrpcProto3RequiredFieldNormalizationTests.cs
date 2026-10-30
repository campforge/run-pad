using Google.Protobuf.Reflection;
using Invoklet.Connectors.Grpc;
using Xunit;

namespace Invoklet.NonUI.Tests.Connectors.Grpc;

// Reproduces the malformed Postman Echo HelloService descriptor (root.proto, syntax "proto3" with
// a LABEL_REQUIRED field) that made protoc reject an otherwise-successfully-reflected schema:
// "Required fields are not allowed in proto3". See handoffs/grpc-postman-invalid-descriptor.md.
public sealed class GrpcProto3RequiredFieldNormalizationTests
{
    private static FieldDescriptorProto Field(string name, FieldDescriptorProto.Types.Label label) => new()
    {
        Name = name,
        Label = label,
        Type = FieldDescriptorProto.Types.Type.String,
    };

    [Fact]
    public void Proto3RequiredField_IsDowngradedToOptional()
    {
        var file = new FileDescriptorProto
        {
            Name = "root.proto",
            Syntax = "proto3",
            MessageType = { new DescriptorProto { Name = "Root", Field = { Field("name", FieldDescriptorProto.Types.Label.Required) } } },
        };

        var normalized = GrpcConnectionDriver.NormalizeProto3RequiredFields(file);

        Assert.Equal(FieldDescriptorProto.Types.Label.Optional, normalized.MessageType[0].Field[0].Label);
        Assert.Equal("proto3", normalized.Syntax); // untouched
        Assert.Equal(FieldDescriptorProto.Types.Label.Required, file.MessageType[0].Field[0].Label); // original unmutated
    }

    [Fact]
    public void Proto3RequiredField_InNestedMessage_IsDowngraded()
    {
        var file = new FileDescriptorProto
        {
            Syntax = "proto3",
            MessageType =
            {
                new DescriptorProto
                {
                    Name = "Outer",
                    NestedType = { new DescriptorProto { Name = "Inner", Field = { Field("id", FieldDescriptorProto.Types.Label.Required) } } },
                },
            },
        };

        var normalized = GrpcConnectionDriver.NormalizeProto3RequiredFields(file);

        Assert.Equal(FieldDescriptorProto.Types.Label.Optional, normalized.MessageType[0].NestedType[0].Field[0].Label);
    }

    [Fact]
    public void Proto2RequiredField_IsLeftAlone()
    {
        var file = new FileDescriptorProto
        {
            Syntax = "proto2",
            MessageType = { new DescriptorProto { Name = "Root", Field = { Field("name", FieldDescriptorProto.Types.Label.Required) } } },
        };

        var normalized = GrpcConnectionDriver.NormalizeProto3RequiredFields(file);

        Assert.Same(file, normalized); // no clone needed: nothing to change
        Assert.Equal(FieldDescriptorProto.Types.Label.Required, normalized.MessageType[0].Field[0].Label);
    }

    [Fact]
    public void Proto3WithoutRequiredFields_IsReturnedUnchanged()
    {
        var file = new FileDescriptorProto
        {
            Syntax = "proto3",
            MessageType = { new DescriptorProto { Name = "Root", Field = { Field("name", FieldDescriptorProto.Types.Label.Optional) } } },
        };

        var normalized = GrpcConnectionDriver.NormalizeProto3RequiredFields(file);

        Assert.Same(file, normalized);
    }
}

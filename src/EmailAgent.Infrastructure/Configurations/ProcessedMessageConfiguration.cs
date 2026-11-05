using EmailAgent.Infrastructure.Entities;
using EmailAgent.Infrastructure.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EmailAgent.Infrastructure.Configurations;

/// <summary>§6 <c>processed_messages</c> mapping (M1T2 task step 8).</summary>
internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(p => p.SourceId).HasColumnName("source_id");
        builder.Property(p => p.RemoteUid).HasColumnName("remote_uid");

        builder.Property(p => p.ReceivedRemoteAt)
            .HasColumnName("received_remote_at")
            .HasColumnType("timestamptz");

        // §6 verbatim: `stage` is text NOT NULL DEFAULT 'fetched' (Q-O9), not
        // a Postgres enum — the enum is converted to its lowercase C# name.
        builder.Property(p => p.Stage)
            .HasColumnName("stage")
            .HasColumnType("text")
            .HasConversion<string>()
            .HasDefaultValue(ProcessedMessageStage.fetched);

        builder.Property(p => p.PrefilterMatched)
            .HasColumnName("prefilter_matched")
            .HasDefaultValue(false);

        builder.Property(p => p.ModelDecisionJson)
            .HasColumnName("model_decision_json")
            .HasColumnType("jsonb");

        builder.Property(p => p.ActionsExecutedJson)
            .HasColumnName("actions_executed_json")
            .HasColumnType("jsonb");

        builder.Property(p => p.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(p => p.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(p => p.TotalTokens).HasColumnName("total_tokens");
        builder.Property(p => p.CostUsd).HasColumnName("cost_usd");

        builder.Property(p => p.BodyBlobId).HasColumnName("body_blob_id");

        builder.Property(p => p.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .HasDefaultValueSql("now()");

        // Dedupe key (§6) — a true UNIQUE constraint (raw SQL in the
        // migration; M1T2 task step 11/12 assert it via pg_constraint).
        builder.HasIndex(p => new { p.SourceId, p.RemoteUid })
            .IsUnique()
            .HasDatabaseName("ux_processed_messages_source_remote_uid");

        builder.HasIndex(p => new { p.SourceId, p.CreatedAt })
            .IsDescending(false, true)
            .HasDatabaseName("ix_processed_messages_source_created");

        // Restrict, NOT Cascade: §6/Q-O15 keep orphan processed_messages rows
        // when the source is deleted (audit-trail-safe).
        builder.HasOne(p => p.Source)
            .WithMany(m => m.ProcessedMessages)
            .HasForeignKey(p => p.SourceId)
            .OnDelete(DeleteBehavior.Restrict);

        // Body blobs are deleted only when unreferenced (orphan-blob sweeper);
        // never cascade a blob delete into the message row.
        builder.HasOne(p => p.BodyBlob)
            .WithMany()
            .HasForeignKey(p => p.BodyBlobId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
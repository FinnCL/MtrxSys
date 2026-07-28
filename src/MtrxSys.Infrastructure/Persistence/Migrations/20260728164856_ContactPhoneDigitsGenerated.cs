using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Troca o índice funcional por uma COLUNA GERADA (<c>phone_digits</c>) indexada.
    /// </summary>
    /// <remarks>
    /// <para>POR QUE. A versão anterior (IX_contacts_phone_digits sobre a expressão) funcionava, mas
    /// forçava o C# a extrair dígitos em MEMÓRIA pra casar com o índice, com dois problemas medidos:
    /// uma varredura da tabela inteira por lookup (o Postgres não usava o índice funcional num
    /// <c>= ANY</c> vindo do EF — dava Seq Scan), e a impossibilidade de filtrar por dígitos em LINQ
    /// (regexp_replace sem 'g' troca só o 1º caractere) ou por FromSql (<c>SELECT *</c> não traz o
    /// <c>xmin</c>, o token de concorrência do Contact).</para>
    ///
    /// <para>Com a coluna gerada, a normalização vira parte do SCHEMA: o Postgres a mantém, o índice é
    /// sobre coluna real (Index Scan, verificado), e o C# consulta com igualdade trivial e entidade
    /// rastreada. <c>GENERATED ALWAYS ... STORED</c> é imutável e não pode ser escrita à mão, então
    /// nunca diverge do phone_e164.</para>
    ///
    /// <para>Sem risco de duplicata na troca: o valor gerado é o MESMO que a expressão do índice antigo
    /// computava, então quem passava lá passa aqui. A base estava limpa quando o índice antigo entrou.</para>
    /// </remarks>
    public partial class ContactPhoneDigitsGenerated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_contacts_phone_digits\";");
            migrationBuilder.Sql(
                "ALTER TABLE contacts ADD COLUMN phone_digits text "
                + "GENERATED ALWAYS AS (regexp_replace(phone_e164, '[^0-9]', '', 'g')) STORED;");
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_contacts_phone_digits\" "
                + "ON contacts (phone_digits) WHERE deleted_at IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_contacts_phone_digits\";");
            migrationBuilder.Sql("ALTER TABLE contacts DROP COLUMN IF EXISTS phone_digits;");
            // Restaura o índice funcional anterior, pra o Down deixar o schema exatamente como estava.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX \"IX_contacts_phone_digits\" "
                + "ON contacts (regexp_replace(phone_e164, '[^0-9]', '', 'g')) WHERE deleted_at IS NULL;");
        }
    }
}

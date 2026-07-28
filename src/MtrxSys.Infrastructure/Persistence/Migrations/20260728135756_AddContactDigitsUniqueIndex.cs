using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MtrxSys.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Índice ÚNICO sobre os DÍGITOS do telefone: impede o mesmo número entrar duas vezes em formatos
    /// diferentes. SQL cru porque é índice funcional — o modelo do EF não expressa expressão em coluna.
    ///
    /// <para>🔴 O QUE ELE IMPEDE, medido em produção 2026-07-27: o mesmo grupo importado pelo WAHA em
    /// 22/07 e pelo emulador em 26/07 gerou 50 contatos DUPLICADOS — "+5588…" e "5588…" tratados como
    /// pessoas diferentes, com 50 jobs a mais na fila. O índice único já existente é sobre `phone_e164`
    /// CRU, e as duas strings são de fato diferentes, então ele não viu problema nenhum.</para>
    ///
    /// <para>A origem já foi corrigida (o import normaliza pra E164), mas isto não depende de a origem
    /// estar certa: contato entra por importação de grupo, adição manual e base herdada. Índice é
    /// garantia; comparação no código é convenção, e convenção some no primeiro caminho novo.</para>
    ///
    /// <para><c>WHERE deleted_at IS NULL</c> de propósito: contato DESCARTADO não deve bloquear a
    /// re-entrada do mesmo número. O efeito colateral é bom — se um descartado for reimportado
    /// (ReimportInto limpa o deleted_at) enquanto existe um ativo com os mesmos dígitos, o banco
    /// recusa e o import registra a falha, em vez de ressuscitar a duplicata em silêncio.</para>
    /// </summary>
    public partial class AddContactDigitsUniqueIndex : Migration
    {
        // regexp_replace/4 é IMMUTABLE no Postgres, requisito pra índice funcional.
        private const string Expression = "(regexp_replace(phone_e164, '[^0-9]', '', 'g'))";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_contacts_phone_digits\" "
                + $"ON contacts {Expression} WHERE deleted_at IS NULL;");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_contacts_phone_digits\";");
    }
}

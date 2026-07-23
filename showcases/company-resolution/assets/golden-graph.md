# Golden organization graph

One golden organization, resolved from five source records across two independent
public systems — SEC EDGAR and GLEIF — with no shared key between them:

```mermaid
graph TD
  G["★ Golden: Apple Inc.<br/>ONE APPLE PARK WAY, CUPERTINO, CA 95014"]
  S1["SEC record<br/>sec-0000320193<br/>'Apple Inc.'<br/>CIK 0000320193"]
  S2["SEC former name<br/>sec-0000320193-former1<br/>'APPLE INC'"]
  S3["SEC former name<br/>sec-0000320193-former2<br/>'APPLE COMPUTER INC'"]
  S4["SEC former name<br/>sec-0000320193-former3<br/>'APPLE COMPUTER INC/ FA'"]
  S5["GLEIF record<br/>gleif-HWUPKR0MPOU8FGXBT394<br/>'Apple Inc.'<br/>LEI HWUPKR0MPOU8FGXBT394"]
  S1 -->|resolved-to| G
  S2 -->|resolved-to| G
  S3 -->|resolved-to| G
  S4 -->|resolved-to| G
  S5 -->|resolved-to| G
```

Every source record shares the same address (One Apple Park Way, Cupertino, CA 95014)
even though the names range from the current legal name to two retired SEC filer
names — and the GLEIF record carries an independently issued LEI with no CIK in
common. Linkuity resolves all five into a single golden organization on fuzzy
name + address alone.

Reproduce the full graph in Neo4j by running the demo with `-Neo4j` and loading
`output/neo4j-export.zip`'s `load.cypher`.

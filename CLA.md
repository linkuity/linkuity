# Linkuity Contributor License Agreement (CLA)

> **Important:** This CLA is provided as a starting point, adapted from the
> widely used Apache Software Foundation Individual and Corporate CLAs. It has
> **not** yet been reviewed by legal counsel for Linkuity. Before relying on it,
> the project owner should have it reviewed and should confirm the legal entity
> name ("Linkuity") and notice/signing process. See "Signing" at the end.

Thank you for your interest in contributing to Linkuity ("the Project"). To
clarify the intellectual-property license granted with Contributions from any
person or entity, the Project must have a Contributor License Agreement (CLA) on
file that has been signed by each Contributor, indicating agreement to the
license terms below. This license is for your protection as a Contributor as
well as the protection of the Project and its users; it does not change your
rights to use your own Contributions for any other purpose.

There are two agreements:

- The **Individual CLA** — for an individual contributing on their own behalf.
- The **Corporate CLA** — for a company or other legal entity that wishes to
  have its employees' Contributions covered.

If you are contributing in the course of work for an employer, or your employer
may otherwise claim rights in your Contributions, both a Corporate CLA (signed
by the entity) and an Individual CLA (signed by you) are required.

---

## Individual Contributor License Agreement

You accept and agree to the following terms and conditions for Your present and
future Contributions submitted to the Project. Except for the license granted
herein to the Project and recipients of software distributed by the Project, You
reserve all right, title, and interest in and to Your Contributions.

1. **Definitions.** "You" (or "Your") means the copyright owner or legal entity
   authorized by the copyright owner that is making this Agreement. "Contribution"
   means any original work of authorship, including any modifications or additions
   to an existing work, that is intentionally submitted by You to the Project for
   inclusion in, or documentation of, any of the products owned or managed by the
   Project (the "Work"). "Submitted" means any form of electronic, verbal, or
   written communication sent to the Project or its representatives, including but
   not limited to communication on electronic mailing lists, source code control
   systems, and issue tracking systems that are managed by, or on behalf of, the
   Project for the purpose of discussing and improving the Work, but excluding
   communication that is conspicuously marked or otherwise designated in writing
   by You as "Not a Contribution."

2. **Grant of Copyright License.** Subject to the terms and conditions of this
   Agreement, You hereby grant to the Project and to recipients of software
   distributed by the Project a perpetual, worldwide, non-exclusive, no-charge,
   royalty-free, irrevocable copyright license to reproduce, prepare derivative
   works of, publicly display, publicly perform, sublicense, and distribute Your
   Contributions and such derivative works.

3. **Grant of Patent License.** Subject to the terms and conditions of this
   Agreement, You hereby grant to the Project and to recipients of software
   distributed by the Project a perpetual, worldwide, non-exclusive, no-charge,
   royalty-free, irrevocable (except as stated in this section) patent license to
   make, have made, use, offer to sell, sell, import, and otherwise transfer the
   Work, where such license applies only to those patent claims licensable by You
   that are necessarily infringed by Your Contribution(s) alone or by combination
   of Your Contribution(s) with the Work to which such Contribution(s) was
   submitted. If any entity institutes patent litigation against You or any other
   entity (including a cross-claim or counterclaim in a lawsuit) alleging that
   Your Contribution, or the Work to which You have contributed, constitutes
   direct or contributory patent infringement, then any patent licenses granted
   to that entity under this Agreement for that Contribution or Work shall
   terminate as of the date such litigation is filed.

4. **Right to Grant.** You represent that You are legally entitled to grant the
   above license. If Your employer(s) has rights to intellectual property that You
   create that includes Your Contributions, You represent that You have received
   permission to make Contributions on behalf of that employer, that Your employer
   has waived such rights for Your Contributions to the Project, or that Your
   employer has executed a separate Corporate CLA with the Project.

5. **Original Work.** You represent that each of Your Contributions is Your
   original creation (see Section 7 for submissions on behalf of others). You
   represent that Your Contribution submissions include complete details of any
   third-party license or other restriction (including, but not limited to,
   related patents and trademarks) of which You are personally aware and which are
   associated with any part of Your Contributions.

6. **No Warranty / No Support Obligation.** You are not expected to provide
   support for Your Contributions, except to the extent You desire to provide
   support. You may provide support for free, for a fee, or not at all. Unless
   required by applicable law or agreed to in writing, You provide Your
   Contributions on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
   KIND, either express or implied, including, without limitation, any warranties
   or conditions of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
   PARTICULAR PURPOSE.

7. **Third-Party Work.** Should You wish to submit work that is not Your original
   creation, You may submit it to the Project separately from any Contribution,
   identifying the complete details of its source and of any license or other
   restriction (including, but not limited to, related patents, trademarks, and
   license agreements) of which You are personally aware, and conspicuously
   marking the work as "Submitted on behalf of a third-party: [named here]."

8. **Notice.** You agree to notify the Project of any facts or circumstances of
   which You become aware that would make these representations inaccurate in any
   respect.

---

## Corporate Contributor License Agreement

This version is for a legal entity (the "Corporation") that has assigned
employees or agents to contribute to the Project. It covers Contributions
submitted by the individuals listed in a schedule the Corporation maintains with
the Project.

The Corporation accepts and agrees to the same terms as Sections 1–8 of the
Individual Contributor License Agreement above, applied to Contributions
submitted by the Corporation's designated employees, with the following
additions:

- **Grants.** The Corporation grants to the Project and to recipients of software
  distributed by the Project the same copyright and patent licenses described in
  Sections 2 and 3 above, for Contributions submitted by its designated
  employees.

- **Right to Grant.** The Corporation represents that it is legally entitled to
  grant the above licenses and that each designated employee is authorized to
  submit Contributions on the Corporation's behalf.

- **Designated Employees.** The Corporation will maintain and provide to the
  Project a list of the employees authorized to submit Contributions on its
  behalf, and will keep that list current.

- **No Warranty.** Contributions are provided on an "AS IS" basis, as described
  in Section 6 above.

---

## Signing

Signing is enforced automatically on each pull request by the CLA Assistant
GitHub Action (`.github/workflows/cla.yml`):

1. Open a pull request. If you have not signed, the bot comments and the CLA
   status check stays red.
2. Read this document, then comment the following on the pull request:

   > I have read the CLA Document and I hereby sign the CLA

3. The bot records your signature (in `signatures/version1/cla.json`) and turns
   the check green. You sign only once; later pull requests are recognized
   automatically. Pull requests cannot be merged until the check passes.

<!--
TODO (maintainers):
  - Have this CLA reviewed by legal counsel and confirm the Linkuity legal
    entity name used throughout.
  - Repo setup for the CLA Assistant workflow (.github/workflows/cla.yml):
      * Create a classic Personal Access Token with `repo` scope and add it as a
        repository secret named PERSONAL_ACCESS_TOKEN (used to commit the
        signatures file).
      * Make the "CLA Assistant" status check required in branch protection so
        unsigned pull requests cannot be merged.
-->


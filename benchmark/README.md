# Retrieval benchmark

How well does the retrieval part of this RAG find the passages that answer a student's question? This folder holds the questions, what the system retrieved for each one, and a verdict on whether the retrieved text was good enough to answer.

- [`judgments.json`](judgments.json): all 60 questions, with the top 5 retrieved section headings, the verdict and a one-line reason for each.

The LLM answer step (`scripts/ask.py`) is not tested here. Only retrieval is.

## Result

Corpus: *Thomas' Calculus, Twelfth Edition (Single Variable)*, a 789-page scanned textbook split into 1322 chunks.

| | Strict (full answer only) | Lenient (full or partial) |
|---|---|---|
| Top 1 | 31.7% | 41.7% |
| Top 3 | 53.3% | 70.0% |
| **Top 5** | **63.3%** | **86.7%** |

Of the 60 questions, 38 got a full answer in the top 5, 14 a partial answer and 8 nothing useful. Mean reciprocal rank (full only) is 0.432.

## How the conclusion was reached

1. **Questions.** An AI coding assistant (Devin) queried the public Stack Exchange API once per topic in the book's table of contents and took the highest-voted calculus questions from [Math StackExchange](https://math.stackexchange.com). It kept 60 that are on topic for the book and clear from the title alone. The titles are copied unedited, and the source link and votes are in `judgments.json`. Only the title is used as the query, never the body, so queries are short and sometimes vague, like a search box.
2. **Retrieval.** Each question was embedded with `qwen3-embedding:0.6b` (using the same instruction prefix as `scripts/ask.py`) and the 5 nearest chunks were taken from Qdrant. Each chunk is one book section, heading plus body, up to 800 words.
3. **Judging.** The same assistant read the text of the retrieved chunks for every question, in order, and decided whether a chunk alone contained the information needed to answer the question:
   - **full**: one chunk alone has what is needed to answer.
   - **partial**: a chunk has part of it (for example the formula without the derivation).
   - **none**: nothing useful in the top 5.
4. **Scoring.** `first_useful_rank` is the rank of the first chunk that earned the verdict. "Top k" means a full (or, for lenient, full or partial) answer within the first k chunks. The numbers above are computed from the `verdict` and `first_useful_rank` fields.

## Where it fails

- **Exercise lists outrank explanations.** Many top-5 slots go to exercise or answer-key chunks, which are short and keyword-dense. Example: for "Struggling to understand epsilon-delta", the first four chunks are exercises and an answer key, and the first real explanation is at rank 5.
- **Definitions cut away from their introduction.** For the epsilon-delta question about limits at infinity, the retrieved chunk ends with "Here are precise definitions", but the definitions are in another chunk that wasn't retrieved.
- **The book itself is the limit.** Many partial and none verdicts are questions whose answer the textbook doesn't contain, such as a proof of Simpson's error bound or the convergence of one specific series. Better retrieval cannot fix those.

## Limitations

- **The judge is an AI.** Every verdict comes from a single pass by one LLM and has not been fully reviewed by a human. Borderline cases, especially full versus partial, could go either way, which is why strict and lenient numbers are both shown.
- **Judged from partial text in places.** To keep the reading manageable, the judge mostly saw the first ~500 characters of each chunk and searched the full text only when a verdict depended on it. Some verdicts could change on a full read.
- **Only the top 5 were judged**, so there is no top-10 number.
- **Questions are restricted to topics the book covers**, so refusals on questions outside the book are not tested.
- **One book and 60 questions.**
- **Spot-checking is easy.** Pick any row in `judgments.json`, open the chunks for that question's `retrieved_top5` headings (in `data/markdown/<hash>/chunks.json`) and compare with the verdict.

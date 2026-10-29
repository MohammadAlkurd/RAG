# /// script
# dependencies = ["ollama", "qdrant-client"]
# ///
import sys
import ollama
from qdrant_client import QdrantClient

question = " ".join(sys.argv[1:])
query = f"Instruct: Given a question, retrieve textbook passages that answer it\nQuery: {question}"
vector = ollama.embed(model="qwen3-embedding:0.6b", input=query, options={"num_gpu": 0}).embeddings[0]
hits = QdrantClient("localhost").query_points("chunks", query=vector, limit=5).points
docs = "\n\n---\n\n".join(hit.payload["Text"] for hit in hits)

prompt = f"{docs}\n\nUsing only the documents above, answer the question. If they don't contain the answer, say so.\n\nQuestion: {question}"
answer = ollama.chat(model="qwen3.5", messages=[{"role": "user", "content": prompt}], options={"num_ctx": 16384}, think=False)
print(answer.message.content)
print("\nSources:", *(hit.payload["Heading"] for hit in hits), sep="\n- ")

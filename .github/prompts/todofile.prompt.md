---
name: todofile
description: Resolve TODO commits in the repo and tasks in TODO.txt
---
Find and resolve all TODO comments in the code in this repo. Do not look at third party code, ensure any searches filter out paths ignored by git. After resolving related TODO comments, delete them then commit and push the changes. For each TODO item, create a separate commit and push it as you go. If there are closely related TODO comments, then combine them into a single commit.

Afterwards, resolve all the tasks in the TODO.txt file at the root of the repo. If the file is divided into Tasks, complete the tasks in order and commit and push each one as it is resolved. Once everything is done, clear the contents of the TODO.txt file and commit and push that change as well.

Test the changes by running the integration tests.

For subsequent activity, do not auto commit until asked to do so by the user or a prompt.
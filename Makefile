.PHONY: check
check:
	python3 -m unittest discover -s tests -v
	python3 -m compileall -q scripts
	shellcheck scripts/*.sh
	git diff --check
